using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record RestorePointInfo(int SequenceNumber, string Description, string CreatedAt);

public sealed class RestorePointService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _createdThisSession;

    public async Task<ActionResult> EnsureBeforeChangeAsync(CancellationToken ct)
    {
        if (_createdThisSession is not null) return new(true, "Backup desta sessão já está pronto.");
        return await CreateAsync("HeatTurbo - antes das otimizações", ct);
    }

    public async Task<ActionResult> CreateAsync(string description, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new(false, "Pontos de restauração só estão disponíveis no Windows.");
        await _gate.WaitAsync(ct);
        try
        {
            var safe = new string(description.Where(c => char.IsLetterOrDigit(c) || " -_.".Contains(c)).Take(80).ToArray());
            var script = $"Checkpoint-Computer -Description '{safe}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'ok'";
            await SystemInfoService.RunPowerShellAsync(script, ct);
            _createdThisSession = DateTimeOffset.Now;
            return new(true, "Ponto de restauração criado com sucesso.");
        }
        catch (Exception ex)
        {
            return new(false, "Não foi possível criar o ponto de restauração. Verifique se a Proteção do Sistema está ativada. " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RestorePointInfo>> GetAllAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];
        const string script = "Get-ComputerRestorePoint | Sort-Object SequenceNumber -Descending | Select-Object -First 10 SequenceNumber,Description,CreationTime | ConvertTo-Json -Compress";
        try
        {
            var json = await SystemInfoService.RunPowerShellAsync(script, ct);
            if (string.IsNullOrWhiteSpace(json)) return [];
            using var doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
            return rows.Select(x => new RestorePointInfo(
                x.GetProperty("SequenceNumber").GetInt32(),
                x.GetProperty("Description").GetString() ?? "Ponto de restauração",
                x.GetProperty("CreationTime").ToString())).ToArray();
        }
        catch { return []; }
    }
}
