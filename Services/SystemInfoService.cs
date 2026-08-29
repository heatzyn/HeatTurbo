using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record SystemSnapshot(
    string Cpu, string Gpu, string Ram, string Disk, string DiskModel,
    string OperatingSystem, string Version, string Uptime, string ComputerName,
    bool IsWindows, DateTimeOffset CapturedAt);

public sealed class SystemInfoService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemSnapshot? _cached;

    public async Task<SystemSnapshot> GetSnapshotAsync(CancellationToken ct, bool refresh = false)
    {
        if (!refresh && _cached is { } value && DateTimeOffset.UtcNow - value.CapturedAt < TimeSpan.FromSeconds(20))
            return value;

        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is { } current && DateTimeOffset.UtcNow - current.CapturedAt < TimeSpan.FromSeconds(20))
                return current;

            _cached = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? await ReadWindowsAsync(ct)
                : ReadPortable();
            return _cached;
        }
        finally { _gate.Release(); }
    }

    private static async Task<SystemSnapshot> ReadWindowsAsync(CancellationToken ct)
    {
        const string script = """
            $cpu=(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
            $gpu=(Get-CimInstance Win32_VideoController | Where-Object {$_.Name -notmatch 'Remote|Basic Display'} | Select-Object -First 1 -ExpandProperty Name)
            $ram=[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,0)
            $disk=Get-CimInstance Win32_DiskDrive | Select-Object -First 1
            $os=Get-CimInstance Win32_OperatingSystem
            [pscustomobject]@{cpu=$cpu;gpu=$gpu;ram=$ram;diskSize=[math]::Round($disk.Size/1GB,0);diskModel=$disk.Model;os=$os.Caption;version=$os.Version;boot=$os.LastBootUpTime} | ConvertTo-Json -Compress
            """;

        var json = await RunPowerShellAsync(script, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var boot = root.TryGetProperty("boot", out var bootEl) && DateTimeOffset.TryParse(bootEl.GetString(), out var parsed)
            ? parsed : DateTimeOffset.Now;
        return new(
            Text(root, "cpu", "CPU não identificada"), Text(root, "gpu", "GPU não identificada"),
            $"{Number(root, "ram")} GB", $"{Number(root, "diskSize")} GB", Text(root, "diskModel", "Disco não identificado"),
            Text(root, "os", "Windows"), Text(root, "version", Environment.OSVersion.Version.ToString()),
            FormatUptime(DateTimeOffset.Now - boot), Environment.MachineName, true, DateTimeOffset.UtcNow);
    }

    private static SystemSnapshot ReadPortable()
    {
        var totalGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d;
        return new(RuntimeInformation.ProcessArchitecture.ToString(), "Disponível somente no Windows",
            $"{totalGb:0.#} GB disponível", "—", "Leitura completa no Windows",
            RuntimeInformation.OSDescription, Environment.OSVersion.Version.ToString(),
            FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), Environment.MachineName, false, DateTimeOffset.UtcNow);
    }

    private static string Text(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString() : fallback;
    private static string Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "—";
    private static string FormatUptime(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h" : $"{(int)value.TotalHours}h {value.Minutes}min";

    internal static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command"); start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Falha no PowerShell." : error.Trim());
        return output.Trim();
    }
}
