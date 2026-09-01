using System.Runtime.InteropServices;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.DRS.SettingValues;

namespace HeatTurbo.Services;

public sealed record NvidiaSettingItem(string Id, string Name, string Description, string Category, bool IsActive);
public sealed record NvidiaCatalog(bool Available, string Message, IReadOnlyList<NvidiaSettingItem> Items);

public sealed class NvidiaControlService : IDisposable
{
    private readonly RestorePointService _restorePoints;
    private readonly bool _available;

    private sealed record Definition(string Id, string Name, string Description, string Category, KnownSettingId SettingId, uint DesiredValue);

    // IDs e valores conferidos com o header oficial NvApiDriverSettings.h (github.com/NVIDIA/nvapi).
    private static readonly Definition[] Definitions =
    [
        new("nv-vsync-off", "Sincronização vertical desligada",
            "Remove a espera pelo sinal do monitor, reduzindo o input lag. O CS2 já limita seus próprios quadros quando necessário.", "Latência",
            KnownSettingId.VSyncMode, (uint)VSyncMode.ForceOff),
        new("nv-power-max-performance", "Gerenciamento de energia em desempenho máximo",
            "Evita que a GPU baixe os clocks entre picos de carga durante a partida.", "Performance",
            KnownSettingId.PreferredPerformanceState, (uint)PreferredPerformanceState.PreferMaximum),
        new("nv-texture-high-performance", "Filtragem de textura em alto desempenho",
            "Prioriza taxa de quadros em vez de qualidade de textura; não altera a resolução do jogo.", "Performance",
            KnownSettingId.QualityEnhancements, (uint)QualityEnhancements.HighPerformance),
        new("nv-ambient-occlusion-off", "Oclusão ambiental desligada",
            "Impede que o driver force oclusão ambiental; o CS2 já controla sua própria iluminação.", "Gaming",
            KnownSettingId.AmbientOcclusionMode, (uint)AmbientOcclusionMode.Off),
        new("nv-anisotropic-app", "Filtragem anisotrópica sob controle do jogo",
            "Impede que o driver force um nível de anisotropia diferente do escolhido nas opções do jogo.", "Gaming",
            KnownSettingId.AnisotropicModeSelector, (uint)AnisotropicModeSelector.Application),
        new("nv-antialiasing-app", "Anti-aliasing sob controle do jogo",
            "Impede que o driver force um modo de AA diferente do escolhido nas opções do jogo.", "Gaming",
            KnownSettingId.AntiAliasingModeSelector, (uint)AntiAliasingModeSelector.ApplicationControl),
    ];

    public NvidiaControlService(RestorePointService restorePoints)
    {
        _restorePoints = restorePoints;
        _available = TryInitialize();
    }

    private static bool TryInitialize()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try { NVIDIA.Initialize(); return true; }
        catch { return false; }
    }

    public Task<NvidiaCatalog> GetAllAsync()
    {
        if (!_available)
            return Task.FromResult(new NvidiaCatalog(false,
                "GPU NVIDIA não detectada ou o driver não expõe o painel de controle (NVAPI).",
                Definitions.Select(x => new NvidiaSettingItem(x.Id, x.Name, x.Description, x.Category, false)).ToArray()));

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.CurrentGlobalProfile;
            var items = Definitions.Select(item =>
            {
                var setting = profile.GetSetting(item.SettingId);
                var active = setting is not null && Convert.ToUInt32(setting.CurrentValue) == item.DesiredValue;
                return new NvidiaSettingItem(item.Id, item.Name, item.Description, item.Category, active);
            }).ToArray();
            return Task.FromResult(new NvidiaCatalog(true, "Perfil global do driver NVIDIA.", items));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new NvidiaCatalog(false, "Não foi possível ler o perfil do driver NVIDIA. " + ex.Message,
                Definitions.Select(x => new NvidiaSettingItem(x.Id, x.Name, x.Description, x.Category, false)).ToArray()));
        }
    }

    public async Task<ActionResult> ApplyAsync(string id, CancellationToken ct)
    {
        if (!_available) return new(false, "Controle NVIDIA indisponível nesta máquina.", id);
        var item = Definitions.FirstOrDefault(x => x.Id == id);
        if (item is null) return new(false, "Ajuste não encontrado.", id);

        var backup = await _restorePoints.EnsureBeforeChangeAsync(ct);
        if (!backup.Success) return backup with { Id = id };

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            session.CurrentGlobalProfile.SetSetting(item.SettingId, item.DesiredValue);
            session.Save();
            return new(true, $"{item.Name} ativado no perfil global NVIDIA.", id);
        }
        catch (Exception ex) { return new(false, ex.Message, id); }
    }

    public Task<ActionResult> RestoreAsync(string id, CancellationToken ct)
    {
        if (!_available) return Task.FromResult(new ActionResult(false, "Controle NVIDIA indisponível nesta máquina.", id));
        var item = Definitions.FirstOrDefault(x => x.Id == id);
        if (item is null) return Task.FromResult(new ActionResult(false, "Ajuste não encontrado.", id));

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            session.CurrentGlobalProfile.RestoreSettingToDefault(item.SettingId);
            session.Save();
            return Task.FromResult(new ActionResult(true, $"{item.Name} restaurado ao padrão do driver NVIDIA.", id));
        }
        catch (Exception ex) { return Task.FromResult(new ActionResult(false, ex.Message, id)); }
    }

    public void Dispose() { if (_available) NVIDIA.Unload(); }
}
