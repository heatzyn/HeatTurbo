using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record DriverInfo(string Device, string Manufacturer, string Version, string Date, string Kind, string OfficialUrl);

public sealed class DriverService
{
    private readonly RestorePointService _restorePoints;
    public DriverService(RestorePointService restorePoints) => _restorePoints = restorePoints;
    public async Task<IReadOnlyList<DriverInfo>> ScanAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];
        const string script = "Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceClass -in @('DISPLAY','SYSTEM') -and $_.DriverVersion} | Sort-Object DeviceClass,DeviceName -Unique | Select-Object DeviceName,Manufacturer,DriverVersion,DriverDate,DeviceClass | ConvertTo-Json -Compress";
        var json = await SystemInfoService.RunPowerShellAsync(script, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        return rows.Take(30).Select(x =>
        {
            var name = Text(x, "DeviceName"); var maker = Text(x, "Manufacturer");
            return new DriverInfo(name, maker, Text(x,"DriverVersion"), Text(x,"DriverDate"), Text(x,"DeviceClass"), OfficialUrl(name + " " + maker));
        }).ToArray();
    }
    public async Task<ActionResult> InstallFromWindowsUpdateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Disponível somente no Windows.");
        var backup = await _restorePoints.EnsureBeforeChangeAsync(ct);
        if (!backup.Success) return backup;
        const string script = "$s=New-Object -ComObject Microsoft.Update.Session;$q=$s.CreateUpdateSearcher().Search(\"IsInstalled=0 and Type='Driver'\");$c=New-Object -ComObject Microsoft.Update.UpdateColl;foreach($u in $q.Updates){if($u.Title -notmatch 'Firmware|BIOS'){if(-not $u.EulaAccepted){$u.AcceptEula()};[void]$c.Add($u)}};if($c.Count -eq 0){'Nenhum driver novo encontrado.';exit 0};$d=$s.CreateUpdateDownloader();$d.Updates=$c;[void]$d.Download();$ready=New-Object -ComObject Microsoft.Update.UpdateColl;foreach($u in $c){if($u.IsDownloaded){[void]$ready.Add($u)}};if($ready.Count -eq 0){throw 'Falha ao baixar os drivers.'};$i=$s.CreateUpdateInstaller();$i.Updates=$ready;$r=$i.Install();\"$($ready.Count) driver(s) processado(s). Reinicialização: $($r.RebootRequired)\"";
        try { return new(true, await SystemInfoService.RunPowerShellAsync(script, ct)); }
        catch (Exception ex) { return new(false, "Windows Update não conseguiu instalar os drivers: " + ex.Message); }
    }
    private static string OfficialUrl(string value) => value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "https://www.nvidia.com/en-us/drivers/" : value.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "https://www.amd.com/en/support/download/drivers.html" : value.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "https://www.intel.com/content/www/us/en/support/detect.html" : "ms-settings:windowsupdate-optionalupdates";
    private static string Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : "—";
}
