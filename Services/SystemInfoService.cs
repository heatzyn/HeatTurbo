using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record SystemSnapshot(
    string Cpu, string Gpu, string Ram, string Disk, string DiskModel,
    string OperatingSystem, string Version, string Uptime, long UptimeSeconds, string ComputerName,
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
            $gpu=(Get-CimInstance Win32_VideoController | Where-Object {$_.Name -notmatch 'Remote|Basic Display'} | Sort-Object @{Expression={if($_.Name -match 'NVIDIA|GeForce|RTX'){0}elseif($_.Name -match 'Radeon RX|Radeon Pro|Intel Arc'){1}elseif($_.Name -match 'Radeon.*Graphics|Intel.*Graphics|UHD|Iris'){9}else{5}}} | Select-Object -First 1 -ExpandProperty Name)
            $ram=[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,0)
            $disk=Get-CimInstance Win32_DiskDrive | Select-Object -First 1
            $os=Get-CimInstance Win32_OperatingSystem
            [pscustomobject]@{cpu=$cpu;gpu=$gpu;ram=$ram;diskSize=[math]::Round($disk.Size/1GB,0);diskModel=$disk.Model;os=$os.Caption;version=$os.Version} | ConvertTo-Json -Compress
            """;

        var json = await RunPowerShellAsync(script, ct, TimeSpan.FromSeconds(45));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // TickCount64 reports the kernel uptime directly and is not affected by clock/time-zone changes.
        var uptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000);
        return new(
            Text(root, "cpu", "CPU não identificada"), Text(root, "gpu", "GPU não identificada"),
            $"{Number(root, "ram")} GB", $"{Number(root, "diskSize")} GB", Text(root, "diskModel", "Disco não identificado"),
            Text(root, "os", "Windows"), Text(root, "version", Environment.OSVersion.Version.ToString()),
            FormatUptime(TimeSpan.FromSeconds(uptimeSeconds)), uptimeSeconds, Environment.MachineName, true, DateTimeOffset.UtcNow);
    }

    private static SystemSnapshot ReadPortable()
    {
        var totalGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d;
        return new(RuntimeInformation.ProcessArchitecture.ToString(), "Disponível somente no Windows",
            $"{totalGb:0.#} GB disponível", "—", "Leitura completa no Windows",
            RuntimeInformation.OSDescription, Environment.OSVersion.Version.ToString(),
            FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), Environment.TickCount64 / 1000, Environment.MachineName, false, DateTimeOffset.UtcNow);
    }

    private static string Text(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString() : fallback;
    private static string Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "—";
    private static string FormatUptime(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h" : $"{(int)value.TotalHours}h {value.Minutes}min";

    internal static async Task<string> RunPowerShellAsync(
        string script,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        using var timeoutSource = new CancellationTokenSource(effectiveTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        var operationToken = linkedSource.Token;

        void ThrowMappedCancellation()
        {
            if (!operationToken.IsCancellationRequested) return;
            if (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"O PowerShell ultrapassou o limite de {effectiveTimeout.TotalMinutes:0.#} minuto(s).");
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(operationToken);
        }

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" + script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        using var cancellationRegistration = operationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(operationToken);
        var errorTask = process.StandardError.ReadToEndAsync(operationToken);
        try
        {
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(operationToken));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            ThrowMappedCancellation();
            throw;
        }
        catch (Exception) when (operationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            ThrowMappedCancellation();
            throw;
        }
        ThrowMappedCancellation();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Falha no PowerShell." : error.Trim());
        return output.Trim();
    }
}
