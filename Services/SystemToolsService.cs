using Microsoft.Win32;

namespace HeatTurbo.Services;

public sealed record ToolsStatus(bool StartWithWindows, bool AutoClean, DateTimeOffset? LastClean, long LastFreedBytes);

public sealed class SystemToolsService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly string _autoCleanFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeatTurbo", "auto-clean.enabled");
    private DateTimeOffset? _lastClean;
    private long _lastFreed;

    public SystemToolsService() => _timer = new System.Threading.Timer(async _ => { if (File.Exists(_autoCleanFlag)) await CleanAsync(CancellationToken.None); }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));

    public ToolsStatus Status() => new(IsStartupEnabled(), File.Exists(_autoCleanFlag), _lastClean, _lastFreed);

    public ActionResult SetStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Disponível somente no Windows.");
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return new(false, "Executável não identificado.");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("HeatTurbo", $"\"{path}\""); else key.DeleteValue("HeatTurbo", false);
        return new(true, enabled ? "HeatTurbo iniciará com o Windows." : "Inicialização automática desativada.");
    }

    public ActionResult SetAutoClean(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_autoCleanFlag)!);
        if (enabled) File.WriteAllText(_autoCleanFlag, "enabled"); else if (File.Exists(_autoCleanFlag)) File.Delete(_autoCleanFlag);
        return new(true, enabled ? "Limpeza automática ativada a cada 6 horas enquanto o app estiver aberto." : "Limpeza automática desativada.");
    }

    public Task<ActionResult> CleanAsync(CancellationToken ct)
    {
        long freed = 0; var cutoff = DateTime.UtcNow.AddHours(-48);
        foreach (var root in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp") }.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                try { var info = new FileInfo(file); if (info.LastWriteTimeUtc < cutoff) { var size=info.Length; info.Delete(); freed += size; } } catch { }
            }
        }
        _lastClean = DateTimeOffset.Now; _lastFreed = freed;
        return Task.FromResult(new ActionResult(true, $"Limpeza concluída: {FormatBytes(freed)} removidos."));
    }

    private static bool IsStartupEnabled() { if (!OperatingSystem.IsWindows()) return false; using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("HeatTurbo") is not null; }
    private static string FormatBytes(long bytes) => bytes >= 1_073_741_824 ? $"{bytes/1_073_741_824d:0.##} GB" : bytes >= 1_048_576 ? $"{bytes/1_048_576d:0.##} MB" : $"{bytes/1024d:0.##} KB";
    public void Dispose() => _timer.Dispose();
}
