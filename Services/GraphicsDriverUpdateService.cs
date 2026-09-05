using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HeatTurbo.Services;

public sealed record DriverUpdateStatus(bool Available, string Message, string InstalledVersion, string? LatestVersion, bool UpdateAvailable, string? DownloadUrl, string? ReleaseDate, string? SizeLabel);

/// <summary>
/// Verifica e baixa o instalador oficial mais recente para a GPU do usuário.
/// A NVIDIA expõe uma API pública que resolve o driver exato para o modelo detectado;
/// AMD e Intel não oferecem o mesmo mecanismo, então para elas baixamos a ferramenta
/// oficial de detecção automática (AMD Software / Intel Driver &amp; Support Assistant),
/// que identifica o hardware por conta própria ao ser executada.
/// </summary>
public sealed class GraphicsDriverUpdateService
{
    private static readonly HttpClient LookupHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    private readonly DriverService _drivers;
    private readonly Dictionary<string, DriverUpdateStatus> _lastCheck = new(StringComparer.OrdinalIgnoreCase);
    private static XDocument? _nvidiaCatalog;
    private static (string Url, string Version)? _amdInstaller;

    public GraphicsDriverUpdateService(DriverService drivers) => _drivers = drivers;

    public Task<DriverUpdateStatus> CheckAsync(string vendor, CancellationToken ct) => vendor.ToLowerInvariant() switch
    {
        "nvidia" => CheckNvidiaAsync(ct),
        "amd" => CheckAmdAsync(ct),
        "intel" => CheckIntelAsync(ct),
        _ => Task.FromResult(new DriverUpdateStatus(false, "Fabricante não suportado.", "—", null, false, null, null, null)),
    };

    public async Task<ActionResult> DownloadAndLaunchAsync(string vendor, CancellationToken ct)
    {
        vendor = vendor.ToLowerInvariant();
        if (!_lastCheck.TryGetValue(vendor, out var status)) status = await CheckAsync(vendor, ct);
        if (!status.Available || string.IsNullOrWhiteSpace(status.DownloadUrl)) return new(false, status.Message);

        try
        {
            var fileName = Path.GetFileName(new Uri(status.DownloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = $"{vendor}-driver-installer.exe";
            var path = Path.Combine(Path.GetTempPath(), fileName);

            using var request = new HttpRequestMessage(HttpMethod.Get, status.DownloadUrl);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            if (vendor == "amd") request.Headers.Referrer = new Uri("https://www.amd.com/en/support/download/drivers.html");

            using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(path))
                await stream.CopyToAsync(file, ct);

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return new(true, $"Instalador baixado. Siga os passos do instalador oficial na tela.");
        }
        catch (Exception ex)
        {
            return new(false, "Falha ao baixar ou abrir o instalador: " + ex.Message);
        }
    }

    // ---------- NVIDIA: resolve o driver exato via a API pública de drivers da NVIDIA ----------

    private async Task<DriverUpdateStatus> CheckNvidiaAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Cache("nvidia", new(false, "Disponível somente no Windows.", "—", null, false, null, null, null));

        var drivers = await _drivers.ScanAsync(ct);
        var gpu = drivers.FirstOrDefault(d => d.Kind == "DISPLAY" && d.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (gpu is null) return Cache("nvidia", new(false, "Nenhuma GPU NVIDIA detectada neste PC.", "—", null, false, null, null, null));

        try
        {
            var (psid, pfid) = await FindNvidiaProductAsync(gpu.Device, ct);
            if (pfid is null)
                return Cache("nvidia", new(false, $"Não foi possível localizar \"{gpu.Device}\" no catálogo de drivers da NVIDIA.", gpu.Version, null, false, null, null, null));

            var osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;
            var lookupUrl = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID={osId}&languageCode=1033&beta=null&isWHQL=1&dltype=-1&dch=1&sort1=0&numberOfResults=1";
            var json = await LookupHttp.GetStringAsync(lookupUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.GetArrayLength() == 0)
                return Cache("nvidia", new(false, "A NVIDIA não retornou nenhum driver para este modelo/sistema operacional.", gpu.Version, null, false, null, null, null));

            var info = ids[0].GetProperty("downloadInfo");
            var latest = info.GetProperty("Version").GetString() ?? "";
            var downloadUrl = info.TryGetProperty("DownloadURL", out var du) ? du.GetString() : null;
            var releaseDate = info.TryGetProperty("ReleaseDateTime", out var rd) ? rd.GetString() : null;
            var sizeLabel = info.TryGetProperty("DownloadURLFileSize", out var sz) ? sz.GetString() : null;

            var updateAvailable = !string.Equals(ToNvidiaMarketingVersion(gpu.Version) ?? gpu.Version, latest, StringComparison.OrdinalIgnoreCase);
            var message = updateAvailable ? $"Driver {latest} disponível (instalado: {gpu.Version})." : "Você já está com o driver mais recente da NVIDIA.";
            return Cache("nvidia", new(true, message, gpu.Version, latest, updateAvailable, downloadUrl, releaseDate, sizeLabel));
        }
        catch (Exception ex)
        {
            return Cache("nvidia", new(false, "Falha ao consultar o site da NVIDIA: " + ex.Message, gpu.Version, null, false, null, null, null));
        }
    }

    private static async Task<(int? Psid, int? Pfid)> FindNvidiaProductAsync(string deviceName, CancellationToken ct)
    {
        // O catálogo da NVIDIA às vezes inclui o prefixo "NVIDIA" no nome do produto (placas recentes)
        // e às vezes não (placas mais antigas), então tentamos as duas formas.
        var stripped = deviceName.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Trim();

        var catalog = _nvidiaCatalog ??= XDocument.Parse(await LookupHttp.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct));
        var entries = catalog.Descendants("LookupValue").ToArray();
        var match = entries.FirstOrDefault(e => string.Equals((string?)e.Element("Name"), deviceName, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => string.Equals((string?)e.Element("Name"), stripped, StringComparison.OrdinalIgnoreCase))
            ?? entries.Where(e =>
            {
                var name = (string?)e.Element("Name");
                return !string.IsNullOrEmpty(name) && (deviceName.Contains(name, StringComparison.OrdinalIgnoreCase) || stripped.Contains(name, StringComparison.OrdinalIgnoreCase));
            }).OrderByDescending(e => ((string?)e.Element("Name"))?.Length ?? 0).FirstOrDefault();
        if (match is null) return (null, null);
        return ((int?)match.Attribute("ParentID"), (int?)match.Element("Value"));
    }

    /// <summary>Converte a versão do driver do Windows (ex.: "32.0.15.6636") para o formato de marketing da NVIDIA (ex.: "566.36").</summary>
    private static string? ToNvidiaMarketingVersion(string windowsDriverVersion)
    {
        var parts = windowsDriverVersion.Split('.');
        if (parts.Length != 4) return null;
        var combined = parts[2].PadLeft(2, '0') + parts[3].PadLeft(4, '0');
        if (combined.Length != 6) return null;
        var trimmed = combined[1..];
        return trimmed[..^2] + "." + trimmed[^2..];
    }

    // ---------- AMD: não há API pública de driver exato; baixamos o instalador de detecção automática da AMD Software ----------

    private async Task<DriverUpdateStatus> CheckAmdAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Cache("amd", new(false, "Disponível somente no Windows.", "—", null, false, null, null, null));

        var drivers = await _drivers.ScanAsync(ct);
        var gpu = drivers.FirstOrDefault(d => d.Kind == "DISPLAY" &&
            (d.Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase) || d.Manufacturer.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)));
        if (gpu is null) return Cache("amd", new(false, "Nenhuma GPU AMD detectada neste PC.", "—", null, false, null, null, null));

        try
        {
            var installer = await FindAmdInstallerAsync(ct);
            if (installer is null)
                return Cache("amd", new(false, "Não foi possível localizar o instalador de detecção automática da AMD no momento.", gpu.Version, null, false, null, null, null));

            var message = $"AMD Software {installer.Value.Version} (detecção automática) disponível. Ele identifica sua GPU e instala o driver certo.";
            return Cache("amd", new(true, message, gpu.Version, installer.Value.Version, true, installer.Value.Url, null, null));
        }
        catch (Exception ex)
        {
            return Cache("amd", new(false, "Falha ao consultar o site da AMD: " + ex.Message, gpu.Version, null, false, null, null, null));
        }
    }

    private static async Task<(string Url, string Version)?> FindAmdInstallerAsync(CancellationToken ct)
    {
        if (_amdInstaller is not null) return _amdInstaller;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.amd.com/en/support/download/drivers.html");
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        using var response = await LookupHttp.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);

        var match = Regex.Match(html, @"https://drivers\.amd\.com/drivers/installer/[^""'\s]+?-(\d+\.\d+\.\d+)-minimalsetup[^""'\s]*?_web\.exe");
        if (!match.Success) return null;

        _amdInstaller = (match.Value, match.Groups[1].Value);
        return _amdInstaller;
    }

    // ---------- Intel: baixamos o Intel Driver & Support Assistant, que detecta e atualiza os drivers Intel instalados ----------

    private async Task<DriverUpdateStatus> CheckIntelAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Cache("intel", new(false, "Disponível somente no Windows.", "—", null, false, null, null, null));

        var drivers = await _drivers.ScanAsync(ct);
        var gpu = drivers.FirstOrDefault(d => d.Kind == "DISPLAY" && d.Manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        if (gpu is null) return Cache("intel", new(false, "Nenhuma GPU Intel detectada neste PC.", "—", null, false, null, null, null));

        const string url = "https://dsadata.intel.com/installer";
        string? version = null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await LookupHttp.SendAsync(head, ct);
            if (response.IsSuccessStatusCode && response.Headers.TryGetValues("X-DSA-Version", out var values))
                version = values.FirstOrDefault();
        }
        catch { /* segue mesmo sem conseguir ler a versão — a URL de download é fixa e estável */ }

        var message = version is not null
            ? $"Intel Driver & Support Assistant {version} disponível. Ele identifica sua GPU e instala o driver certo."
            : "Intel Driver & Support Assistant disponível. Ele identifica sua GPU e instala o driver certo.";
        return Cache("intel", new(true, message, gpu.Version, version, true, url, null, null));
    }

    private DriverUpdateStatus Cache(string vendor, DriverUpdateStatus status)
    {
        _lastCheck[vendor] = status;
        return status;
    }
}
