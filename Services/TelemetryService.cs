using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record TelemetrySnapshot(double Cpu, double Gpu, double Ram, double Disk, DateTimeOffset CapturedAt);

public sealed class TelemetryService : IDisposable
{
    private readonly SemaphoreSlim _performanceGate = new(1, 1);
    private readonly System.Threading.Timer _performanceTimer;
    private readonly object _cpuLock = new();
    private ulong _previousIdle, _previousKernel, _previousUser;
    private double _gpu, _disk;
    private int _disposed;

    public TelemetryService()
    {
        ReadCpu();
        _performanceTimer = new System.Threading.Timer(
            state => { _ = RefreshPerformanceAsync(); }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1500));
    }

    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct) =>
        Task.FromResult(new TelemetrySnapshot(ReadCpu(), Volatile.Read(ref _gpu), ReadRam(), Volatile.Read(ref _disk), DateTimeOffset.UtcNow));

    private double ReadCpu()
    {
        lock (_cpuLock)
        {
            if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            var i = ToUInt64(idle); var k = ToUInt64(kernel); var u = ToUInt64(user);
            var total = k - _previousKernel + u - _previousUser;
            var idleDelta = i - _previousIdle;
            _previousIdle = i; _previousKernel = k; _previousUser = u;
            return total == 0 ? 0 : Math.Clamp(Math.Round((total - idleDelta) * 100d / total, 1), 0, 100);
        }
    }

    private static double ReadRam()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    private static async Task<(double Gpu, double Disk)?> ReadPerformanceDataAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string script = """
            $gpu=$null
            $nvidia=Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
            if($null-ne$nvidia){
              $lines=& $nvidia.Source '--query-gpu=utilization.gpu' '--format=csv,noheader,nounits' 2>$null
              if($LASTEXITCODE-eq 0){
                $samples=@($lines|ForEach-Object{$parsed=0.0;if([double]::TryParse(([string]$_).Trim(),[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$parsed)){$parsed}})
                if($samples.Count-gt 0){$gpu=($samples|Measure-Object -Maximum).Maximum}
              }
            }
            if($null-eq$gpu){
              $engines=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue|Where-Object {$_.Name-match'engtype_3D'}|Measure-Object UtilizationPercentage -Maximum
              $gpu=[double]$engines.Maximum
            }
            $d=Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" -ErrorAction SilentlyContinue
            [pscustomobject]@{gpu=[math]::Min(100,[double]$gpu);disk=[math]::Min(100,[double]$d.PercentDiskTime)}|ConvertTo-Json -Compress
            """;
        try
        {
            using var doc = JsonDocument.Parse(await SystemInfoService.RunPowerShellAsync(
                script, ct, TimeSpan.FromSeconds(10)));
            return (Number(doc.RootElement,"gpu"), Number(doc.RootElement,"disk"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task RefreshPerformanceAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !await _performanceGate.WaitAsync(0)) return;
        try
        {
            var values = await ReadPerformanceDataAsync(CancellationToken.None);
            if (values is { } sample)
            {
                Volatile.Write(ref _gpu, sample.Gpu);
                Volatile.Write(ref _disk, sample.Disk);
            }
        }
        catch { }
        finally { _performanceGate.Release(); }
    }

    private static double Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? Math.Clamp(Math.Round(n,1),0,100) : 0;
    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length; public uint MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _performanceTimer.Dispose();
        // A callback may still be releasing the gate; disposing it here can crash the process.
    }
}
