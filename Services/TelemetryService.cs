using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record TelemetrySnapshot(double Cpu, double Gpu, double Ram, double Disk, DateTimeOffset CapturedAt);

public sealed class TelemetryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ulong _previousIdle, _previousKernel, _previousUser;
    private TelemetrySnapshot? _cached;

    public TelemetryService() => ReadCpu();

    public async Task<TelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        if (_cached is { } c && DateTimeOffset.UtcNow - c.CapturedAt < TimeSpan.FromMilliseconds(900)) return c;
        await _gate.WaitAsync(ct);
        try
        {
            var cpu = ReadCpu();
            var ram = ReadRam();
            var (gpu, disk) = await ReadPerformanceDataAsync(ct);
            return _cached = new(cpu, gpu, ram, disk, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private double ReadCpu()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var i = ToUInt64(idle); var k = ToUInt64(kernel); var u = ToUInt64(user);
        var total = k - _previousKernel + u - _previousUser;
        var idleDelta = i - _previousIdle;
        _previousIdle = i; _previousKernel = k; _previousUser = u;
        return total == 0 ? 0 : Math.Clamp(Math.Round((total - idleDelta) * 100d / total, 1), 0, 100);
    }

    private static double ReadRam()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    private static async Task<(double Gpu, double Disk)> ReadPerformanceDataAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        const string script = "$g=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue|Where-Object {$_.Name -match 'engtype_3D'}|Measure-Object UtilizationPercentage -Maximum;$d=Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter \"Name='_Total'\" -ErrorAction SilentlyContinue;[pscustomobject]@{gpu=[math]::Min(100,[double]$g.Maximum);disk=[math]::Min(100,[double]$d.PercentDiskTime)}|ConvertTo-Json -Compress";
        try
        {
            using var doc = JsonDocument.Parse(await SystemInfoService.RunPowerShellAsync(script, ct));
            return (Number(doc.RootElement,"gpu"), Number(doc.RootElement,"disk"));
        }
        catch { return (0, 0); }
    }

    private static double Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? Math.Clamp(Math.Round(n,1),0,100) : 0;
    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length; public uint MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
