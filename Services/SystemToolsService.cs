using Microsoft.Win32;
using System.Diagnostics;
using System.Xml.Linq;

namespace HeatTurbo.Services;

public sealed record ToolsStatus(bool StartWithWindows, bool AutoClean, DateTimeOffset? LastClean, long LastFreedBytes);

public sealed class SystemToolsService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _cleanGate = new(1, 1);
    private readonly string _autoCleanFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeatTurbo", "auto-clean.enabled");
    private DateTimeOffset? _lastClean;
    private long _lastFreed;

    public SystemToolsService() => _timer = new System.Threading.Timer(
        state => { _ = RunScheduledCleanAsync(); }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));

    public ToolsStatus Status() => new(IsStartupEnabled(), File.Exists(_autoCleanFlag), _lastClean, _lastFreed);

    public ActionResult SetStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Disponível somente no Windows.");
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.GetFileName(path).Equals("HeatTurbo.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, "A inicialização automática só pode ser ativada no HeatTurbo instalado.");

        try
        {
            if (enabled)
            {
                var result = RunSchtasks("/Create", "/TN", "HeatTurbo", "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", $"\"{path}\"", "/F");
                if (result.ExitCode != 0) return new(false, $"O Agendador de Tarefas recusou a configuração: {result.Message}");
            }
            else
            {
                var result = RunSchtasks("/Delete", "/TN", "HeatTurbo", "/F");
                if (result.ExitCode != 0 && StartupTaskExists())
                    return new(false, $"Não foi possível remover a inicialização automática: {result.Message}");
            }

            // Remove the legacy Run entry: elevated apps launched there are commonly blocked by Windows.
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key?.DeleteValue("HeatTurbo", false);
            if (IsStartupTaskPresent() != enabled)
                return new(false, "O Windows não confirmou a alteração da inicialização automática.");
            return new(true, enabled ? "HeatTurbo iniciará com o Windows pelo Agendador de Tarefas." : "Inicialização automática desativada.");
        }
        catch (Exception ex)
        {
            return new(false, $"Não foi possível configurar a inicialização automática: {ex.Message}");
        }
    }

    public ActionResult SetAutoClean(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_autoCleanFlag)!);
            if (enabled) File.WriteAllText(_autoCleanFlag, "enabled");
            else if (File.Exists(_autoCleanFlag)) File.Delete(_autoCleanFlag);
            return new(true, enabled ? "Limpeza automática ativada a cada 6 horas enquanto o app estiver aberto." : "Limpeza automática desativada.");
        }
        catch (Exception ex)
        {
            return new(false, $"Não foi possível salvar a configuração da limpeza automática: {ex.Message}");
        }
    }

    public async Task<ActionResult> CleanAsync(CancellationToken ct)
    {
        if (!await _cleanGate.WaitAsync(0, ct)) return new(false, "Uma limpeza já está em andamento.");
        try
        {
            long freed = 0;
            var cutoff = DateTime.UtcNow.AddHours(-48);
            var roots = new[]
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                freed += CleanDirectoryTree(root, cutoff, ct);
            }

            _lastClean = DateTimeOffset.Now;
            _lastFreed = freed;
            return new(true, $"Limpeza concluída: {FormatBytes(freed)} removidos de temporários com mais de 48 horas.");
        }
        finally
        {
            _cleanGate.Release();
        }
    }

    private async Task RunScheduledCleanAsync()
    {
        if (!File.Exists(_autoCleanFlag)) return;
        try { await CleanAsync(CancellationToken.None); }
        catch { /* A maintenance callback must never terminate the desktop process. */ }
    }

    private static long CleanDirectoryTree(string root, DateTime cutoff, CancellationToken ct)
    {
        long freed = 0;
        var pending = new Stack<string>();
        var visited = new List<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            visited.Add(directory);

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc >= cutoff) continue;
                        var size = info.Length;
                        info.Delete();
                        freed += size;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        foreach (var directory in visited.OrderByDescending(path => path.Length))
        {
            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc < cutoff && !info.EnumerateFileSystemInfos().Any()) info.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return freed;
    }

    private static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        // A entrada Run legada não consegue iniciar de forma confiável um executável que exige elevação.
        return IsStartupTaskPresent();
    }

    private static bool StartupTaskExists() => RunSchtasks("/Query", "/TN", "HeatTurbo").ExitCode == 0;

    private static bool IsStartupTaskPresent()
    {
        var expectedPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(expectedPath)) return false;
        var result = RunSchtasks("/Query", "/TN", "HeatTurbo", "/XML");
        if (result.ExitCode != 0) return false;
        try
        {
            var document = XDocument.Parse(result.Message);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            var command = document.Descendants(ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
            var enabledText = document.Descendants(ns + "Enabled").FirstOrDefault()?.Value;
            var enabled = !bool.TryParse(enabledText, out var parsedEnabled) || parsedEnabled;
            return enabled && !string.IsNullOrWhiteSpace(command) &&
                   Path.GetFullPath(command).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static (int ExitCode, string Message) RunSchtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Agendador de Tarefas indisponível.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "o Agendador de Tarefas não respondeu em 10 segundos");
        }
        Task.WaitAll(output, error);
        var message = string.Join(" ", new[] { output.Result.Trim(), error.Result.Trim() }.Where(value => value.Length > 0));
        return (process.ExitCode, string.IsNullOrWhiteSpace(message) ? $"código {process.ExitCode}" : message);
    }

    private static string FormatBytes(long bytes) => bytes >= 1_073_741_824 ? $"{bytes/1_073_741_824d:0.##} GB" : bytes >= 1_048_576 ? $"{bytes/1_048_576d:0.##} MB" : $"{bytes/1024d:0.##} KB";
    public void Dispose() => _timer.Dispose();
}
