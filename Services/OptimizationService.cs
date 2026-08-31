using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record OptimizationItem(string Id, string Name, string Description, string Category, bool RequiresRestart, bool IsActive);
public sealed record OptimizationProfile(string Id, string Name, string Description, int AdjustmentCount, bool RequiresRestart);
public sealed record ActionResult(bool Success, string Message, string? Id = null);

public sealed class OptimizationService
{
    private readonly RestorePointService _restorePoints;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeatTurbo", "optimization-state.json");

    public OptimizationService(RestorePointService restorePoints) => _restorePoints = restorePoints;

    private sealed record RegistryValue(string Name, string Type, string ApplyExpression, string TestExpression);
    private sealed record Definition(
        string Id,
        string Name,
        string Description,
        string Category,
        bool Restart,
        string Test,
        string Apply,
        string Capture,
        Func<string, string> RestoreCaptured,
        string LegacyRestore);

    private sealed record ProfileDefinition(string Id, string Name, string Description, string[] Items);

    private sealed class OptimizationState
    {
        public OptimizationState() { }
        public Dictionary<string, string> Baselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Applied { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static RegistryValue Dword(string name, string valueExpression, string? testExpression = null) =>
        new(name, "DWord", valueExpression, testExpression ?? $"$value -eq ({valueExpression})");

    private static RegistryValue TextValue(string name, string value) =>
        new(name, "String", QuotePowerShell(value), $"[string]$value -eq {QuotePowerShell(value)}");

    private static readonly Definition[] Definitions =
    [
        RegistrySetting("game-mode", "Modo de Jogo do Windows",
            "Prioriza recursos para o jogo em primeiro plano.", "Gaming", false,
            @"HKCU:\Software\Microsoft\GameBar",
            Dword("AutoGameModeEnabled", "1")),

        RegistrySetting("game-dvr", "Desativar gravação em segundo plano",
            "Evita que o Game DVR grave partidas sem você pedir.", "Gaming", false,
            @"HKCU:\System\GameConfigStore",
            Dword("GameDVR_Enabled", "0")),

        RegistrySetting("game-capture", "Desativar captura de jogos do Windows",
            "Desliga a captura em segundo plano do Windows sem remover a Game Bar.", "Gaming", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR",
            Dword("AppCaptureEnabled", "0")),

        RegistrySetting("mouse-acceleration", "Desativar aceleração do mouse",
            "Mantém o movimento do ponteiro consistente; o CS2 com entrada bruta pode ignorar este ajuste.", "Latência", false,
            @"HKCU:\Control Panel\Mouse",
            TextValue("MouseSpeed", "0"),
            TextValue("MouseThreshold1", "0"),
            TextValue("MouseThreshold2", "0")),

        ActivePowerScheme(),

        RegistrySetting("hags", "Agendamento de GPU por hardware (HAGS)",
            "Permite testar o scheduler de GPU do Windows em drivers WDDM compatíveis; o resultado varia por PC.", "Gaming", true,
            @"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            Dword("HwSchMode", "2")),

        RegistrySetting("background-apps", "Limitar apps da Store em segundo plano",
            "Reduz atividade de aplicativos UWP que você não está usando durante o jogo.", "Performance", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
            Dword("GlobalUserDisabled", "1")),

        RegistrySetting("visual-effects", "Priorizar desempenho visual do Windows",
            "Reduz animações e efeitos da interface para diminuir o trabalho do desktop.", "Performance", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
            Dword("VisualFXSetting", "2")),

        RegistrySetting("power-throttling", "Desativar Power Throttling",
            "Evita economia de energia agressiva em processos enquanto o perfil estiver ativo.", "Latência", true,
            @"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
            Dword("PowerThrottlingOff", "1")),

        RegistrySetting("xbox-overlay", "Desativar atalho da Xbox Game Bar",
            "Evita que a sobreposição abra pelo botão do controle durante a partida.", "Gaming", false,
            @"HKCU:\Software\Microsoft\GameBar",
            Dword("UseNexusForGameBarEnabled", "0")),

        RegistrySetting("transparency", "Desativar transparência do Windows",
            "Reduz composição visual no desktop sem alterar arquivos do sistema.", "Interface", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            Dword("EnableTransparency", "0")),

        RegistrySetting("menu-delay", "Reduzir atraso dos menus",
            "Deixa menus do Windows mais responsivos; não altera o desempenho do jogo.", "Interface", false,
            @"HKCU:\Control Panel\Desktop",
            TextValue("MenuShowDelay", "10")),

        RegistrySetting("startup-delay", "Remover atraso dos apps de inicialização",
            "Inicia aplicativos autorizados logo após a entrada no Windows.", "Inicialização", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
            Dword("StartupDelayInMSec", "0")),

        RegistrySetting("mmcss", "Perfil multimídia responsivo",
            "Mantém a reserva recomendada para tarefas de sistema durante cargas multimídia.", "Latência", true,
            @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Dword("SystemResponsiveness", "10")),

        RegistrySetting("games-task", "Prioridade MMCSS para jogos",
            "Configura a tarefa Games do agendador multimídia para alta prioridade.", "Gaming", true,
            @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            Dword("Priority", "6"),
            TextValue("Scheduling Category", "High"),
            TextValue("SFIO Priority", "High")),

        RegistrySetting("network-throttle", "Remover throttling multimídia de rede",
            "Remove a limitação periódica do processamento multimídia; teste a latência antes e depois.", "Rede", true,
            @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Dword("NetworkThrottlingIndex", "[uint32]::MaxValue", "$value -eq -1 -or [string]$value -eq '4294967295'")),

        RegistrySetting("delivery-optimization", "Desativar compartilhamento P2P de updates",
            "Impede uploads de atualizações do Windows para outros computadores.", "Rede", false,
            @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
            Dword("DODownloadMode", "0")),

        RegistrySetting("windows-suggestions", "Desativar sugestões do Windows",
            "Reduz conteúdo promocional e tarefas sugeridas em segundo plano.", "Geral", false,
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
            Dword("SubscribedContent-338388Enabled", "0"),
            Dword("SoftLandingEnabled", "0")),

        RegistrySetting("edge-background", "Impedir o Edge de ficar em segundo plano",
            "Desativa o Startup Boost e a execução em segundo plano do Microsoft Edge por política local.", "Performance", true,
            @"HKLM:\SOFTWARE\Policies\Microsoft\Edge",
            Dword("StartupBoostEnabled", "0"),
            Dword("BackgroundModeEnabled", "0")),

        PowerSetting("usb-suspend", "Desativar suspensão seletiva USB",
            "Evita economia de energia agressiva em periféricos USB enquanto o computador está na tomada.", "Latência",
            "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0),

        PowerSetting("pcie-link-state", "Desativar economia PCI Express",
            "Evita a latência de saída do estado de economia PCIe enquanto o computador está na tomada.", "Latência",
            "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0),

        PowerSetting("core-parking", "Manter núcleos disponíveis na tomada",
            "Reduz o estacionamento de núcleos no plano atual somente quando conectado à energia.", "Performance",
            "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100)
    ];

    private static readonly ProfileDefinition[] Profiles =
    [
        new("balanced", "Equilibrado",
            "Reduz gravações e tarefas dispensáveis sem alterar o plano de energia.",
            ["game-mode", "game-dvr", "game-capture", "xbox-overlay", "background-apps", "delivery-optimization", "windows-suggestions", "edge-background"]),
        new("competitive", "Competitivo / CS2",
            "Soma ajustes de energia e latência ao modo equilibrado. Reinicie antes de comparar benchmarks.",
            ["game-mode", "game-dvr", "game-capture", "xbox-overlay", "mouse-acceleration", "background-apps", "delivery-optimization", "windows-suggestions", "edge-background", "high-performance", "power-throttling", "games-task", "usb-suspend", "pcie-link-state", "core-parking"])
    ];

    public IReadOnlyList<OptimizationProfile> GetProfiles() => Profiles.Select(profile =>
    {
        var definitions = profile.Items.Select(FindDefinition).Where(x => x is not null).Cast<Definition>().ToArray();
        return new OptimizationProfile(profile.Id, profile.Name, profile.Description, definitions.Length, definitions.Any(x => x.Restart));
    }).ToArray();

    public async Task<IReadOnlyList<OptimizationItem>> GetAllAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Definitions.Select(ToInactiveItem).ToArray();

        var tests = new StringBuilder("$results=[ordered]@{};");
        foreach (var item in Definitions)
        {
            var id = QuotePowerShell(item.Id);
            tests.Append("try{$result=&{").Append(item.Test).Append("};$results[")
                .Append(id).Append("]=([string]$result).Trim().Equals('true',[StringComparison]::OrdinalIgnoreCase)}")
                .Append("catch{$results[").Append(id).Append("]=$false};");
        }
        tests.Append("$results|ConvertTo-Json -Compress");

        var json = await SystemInfoService.RunPowerShellAsync(tests.ToString(), ct);
        var states = JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
            ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        return Definitions.Select(item => new OptimizationItem(
            item.Id, item.Name, item.Description, item.Category, item.Restart,
            states.TryGetValue(item.Id, out var active) && active)).ToArray();
    }

    public async Task<ActionResult> ApplyAsync(string id, CancellationToken ct)
    {
        var backup = await _restorePoints.EnsureBeforeChangeAsync(ct);
        if (!backup.Success) return backup with { Id = id };
        return await ExecuteAsync(id, apply: true, ct);
    }

    public Task<ActionResult> RestoreAsync(string id, CancellationToken ct) => ExecuteAsync(id, apply: false, ct);

    public Task<ActionResult> ApplyCs2ProfileAsync(CancellationToken ct) => ApplyProfileAsync("competitive", ct);

    public async Task<ActionResult> ApplyProfileAsync(string profileId, CancellationToken ct)
    {
        var profile = Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return new(false, "Perfil de otimização não encontrado.", profileId);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new(false, "Os perfis só podem ser aplicados no Windows.", profileId);

        var backup = await _restorePoints.EnsureBeforeChangeAsync(ct);
        if (!backup.Success) return backup with { Id = profileId };

        var applied = 0;
        var failures = new List<string>();
        foreach (var id in profile.Items)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(id, apply: true, ct);
            if (result.Success) applied++;
            else failures.Add($"{id}: {result.Message}");
        }

        return failures.Count == 0
            ? new(true, $"Perfil {profile.Name} aplicado: {applied} ajustes. Nenhum ganho fixo de FPS é garantido; compare nas mesmas condições.", profile.Id)
            : new(false, $"{applied} ajustes aplicados. Falhas: {string.Join(" | ", failures)}", profile.Id);
    }

    public async Task<ActionResult> RestoreAllAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new(false, "Disponível somente no Windows.");
        var state = await ReadStateAsync(ct);
        var ids = state.Applied.ToArray();
        if (ids.Length == 0) return new(true, "Nenhuma otimização aplicada pelo HeatTurbo precisa ser restaurada.");

        var restored = 0;
        var failures = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(id, apply: false, ct);
            if (result.Success) restored++;
            else failures.Add($"{id}: {result.Message}");
        }

        return failures.Count == 0
            ? new(true, $"{restored} otimizações voltaram ao estado anterior salvo pelo HeatTurbo.")
            : new(false, $"{restored} otimizações restauradas. Falhas: {string.Join(" | ", failures)}");
    }

    private async Task<ActionResult> ExecuteAsync(string id, bool apply, CancellationToken ct)
    {
        var item = FindDefinition(id);
        if (item is null) return new(false, "Ajuste não encontrado.", id);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new(false, "Os ajustes só podem ser aplicados no Windows.", id);

        try
        {
            string command;
            string? baseline = null;
            if (apply)
            {
                await EnsureBaselineAsync(item, ct);
                command = item.Apply;
            }
            else
            {
                var state = await ReadStateAsync(ct);
                if (!state.Baselines.TryGetValue(item.Id, out baseline))
                    return new(false,
                        $"O HeatTurbo não possui o estado original salvo para {item.Name}. " +
                        "Nenhuma configuração foi removida; use um ponto de restauração se esse ajuste veio de uma versão antiga.", id);
                command = item.RestoreCaptured(baseline);
            }

            await SystemInfoService.RunPowerShellAsync(command, ct);

            if (apply)
            {
                var verified = (await SystemInfoService.RunPowerShellAsync(item.Test, ct)).Trim()
                    .Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!verified)
                {
                    var rolledBack = await TryRollbackAsync(item, ct);
                    return new(false, rolledBack
                        ? $"O Windows não confirmou a aplicação de {item.Name}; o estado anterior foi restaurado."
                        : $"O Windows não confirmou a aplicação de {item.Name} e a reversão automática falhou. Use Restaurar tudo ou o ponto de restauração.", id);
                }
            }
            else
            {
                var verified = baseline is not null
                    ? SnapshotsMatch(baseline, await SystemInfoService.RunPowerShellAsync(item.Capture, ct))
                    : !(await SystemInfoService.RunPowerShellAsync(item.Test, ct)).Trim()
                        .Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!verified) return new(false, $"O Windows não confirmou a restauração de {item.Name}.", id);
            }

            await MarkAppliedAsync(item.Id, apply, ct);
            var restart = item.Restart ? " Reinicie o Windows para concluir." : string.Empty;
            return new(true, apply ? $"{item.Name} ativado.{restart}" : $"{item.Name} voltou ao estado anterior salvo.{restart}", id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (apply)
            {
                try
                {
                    var rolledBack = await TryRollbackAsync(item, CancellationToken.None);
                    return new(false, rolledBack
                        ? "Operação cancelada e estado anterior restaurado."
                        : "Operação cancelada, mas a reversão automática não foi confirmada. Use Restaurar todos ou o ponto de restauração.", id);
                }
                catch
                {
                    return new(false,
                        "Operação cancelada e a reversão automática falhou. Use Restaurar todos ou o ponto de restauração.", id);
                }
            }
            return new(false, "Operação cancelada; nenhuma restauração foi confirmada.", id);
        }
        catch (Exception ex)
        {
            if (apply)
            {
                try { await TryRollbackAsync(item, CancellationToken.None); }
                catch { }
            }
            return new(false, FriendlyError(ex), id);
        }
    }

    private async Task<bool> TryRollbackAsync(Definition item, CancellationToken ct)
    {
        var state = await ReadStateAsync(ct);
        if (!state.Baselines.TryGetValue(item.Id, out var original)) return false;

        var restored = false;
        try
        {
            await SystemInfoService.RunPowerShellAsync(item.RestoreCaptured(original), ct);
            restored = SnapshotsMatch(original, await SystemInfoService.RunPowerShellAsync(item.Capture, ct));
        }
        catch
        {
            restored = false;
        }

        // A failed or partial mutation must remain visible to Restore All.
        await MarkAppliedAsync(item.Id, applied: !restored, ct);
        return restored;
    }

    private async Task EnsureBaselineAsync(Definition item, CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            var state = await LoadStateFileAsync(ct);
            if (state.Baselines.ContainsKey(item.Id)) return;

            var baseline = await SystemInfoService.RunPowerShellAsync(item.Capture, ct);
            if (string.IsNullOrWhiteSpace(baseline)) throw new InvalidOperationException("Não foi possível registrar o estado original do ajuste.");
            state.Baselines[item.Id] = baseline;
            await SaveStateFileAsync(state, ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<OptimizationState> ReadStateAsync(CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct);
        try { return await LoadStateFileAsync(ct); }
        finally { _stateGate.Release(); }
    }

    private async Task MarkAppliedAsync(string id, bool applied, CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            var state = await LoadStateFileAsync(ct);
            if (applied)
            {
                state.Applied.Add(id);
            }
            else
            {
                state.Applied.Remove(id);
                state.Baselines.Remove(id);
            }
            await SaveStateFileAsync(state, ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<OptimizationState> LoadStateFileAsync(CancellationToken ct)
    {
        if (!File.Exists(_statePath)) return new();
        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<OptimizationState>(stream, cancellationToken: ct) ?? new();
            state.Baselines = new Dictionary<string, string>(state.Baselines ?? [], StringComparer.OrdinalIgnoreCase);
            state.Applied = new HashSet<string>(state.Applied ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                "O arquivo com os estados originais das otimizações está corrompido. " +
                "As alterações foram bloqueadas para não sobrescrever o caminho de volta.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "O HeatTurbo não conseguiu ler os estados originais das otimizações. " +
                "As alterações foram bloqueadas para preservar a restauração.", error);
        }
    }

    private async Task SaveStateFileAsync(OptimizationState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"optimization-state.{Environment.ProcessId}.tmp");
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static Definition RegistrySetting(
        string id, string name, string description, string category, bool restart,
        string path, params RegistryValue[] values)
    {
        var quotedPath = QuotePowerShell(path);
        var testParts = values.Select(value =>
            $"$value=$key.GetValue({QuotePowerShell(value.Name)},$null);if(-not({value.TestExpression})){{return 'false'}};");
        var test = $"$key=Get-Item -LiteralPath {quotedPath} -ErrorAction SilentlyContinue;if($null-eq$key){{'false';return}};" +
                   string.Concat(testParts) + "'true'";

        var apply = $"New-Item -Path {quotedPath} -Force|Out-Null;" + string.Concat(values.Select(value =>
            $"New-ItemProperty -LiteralPath {quotedPath} -Name {QuotePowerShell(value.Name)} -PropertyType {value.Type} -Value ({value.ApplyExpression}) -Force|Out-Null;"));

        var capture = $"$key=Get-Item -LiteralPath {quotedPath} -ErrorAction SilentlyContinue;$items=@();" +
            string.Concat(values.Select(value =>
                $"$exists=$null-ne$key -and $key.GetValueNames()-contains {QuotePowerShell(value.Name)};" +
                $"$items+=[pscustomobject]@{{name={QuotePowerShell(value.Name)};exists=$exists;kind=$(if($exists){{$key.GetValueKind({QuotePowerShell(value.Name)}).ToString()}}else{{$null}});value=$(if($exists){{$key.GetValue({QuotePowerShell(value.Name)})}}else{{$null}})}};")) +
            "ConvertTo-Json -Compress -InputObject @($items)";

        string RestoreCaptured(string baseline)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(baseline));
            return $"$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'));$state=$json|ConvertFrom-Json;" +
                   $"foreach($entry in @($state)){{if([bool]$entry.exists){{New-Item -Path {quotedPath} -Force|Out-Null;" +
                   $"New-ItemProperty -LiteralPath {quotedPath} -Name ([string]$entry.name) -PropertyType ([Microsoft.Win32.RegistryValueKind]$entry.kind) -Value $entry.value -Force|Out-Null}}" +
                   $"else{{Remove-ItemProperty -LiteralPath {quotedPath} -Name ([string]$entry.name) -ErrorAction SilentlyContinue}}}}";
        }

        var legacyRestore = string.Concat(values.Select(value =>
            $"Remove-ItemProperty -LiteralPath {quotedPath} -Name {QuotePowerShell(value.Name)} -ErrorAction SilentlyContinue;"));
        return new(id, name, description, category, restart, test, apply, capture, RestoreCaptured, legacyRestore);
    }

    private static Definition ActivePowerScheme()
    {
        const string id = "high-performance";
        const string highPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        const string readScheme = "$output=powercfg.exe /getactivescheme;if($LASTEXITCODE-ne 0){throw ($output-join ' ')};$match=[regex]::Match(($output-join ' '),'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}');if(-not$match.Success){throw 'Plano de energia ativo não identificado.'};$match.Value.ToLowerInvariant()";

        string RestoreCaptured(string baseline)
        {
            var scheme = Guid.TryParse(baseline.Trim(), out var parsed) ? parsed.ToString() : balanced;
            return $"$output=powercfg.exe /setactive {scheme};if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}}";
        }

        return new(id, "Plano de energia de alto desempenho",
            "Reduz economia agressiva de energia enquanto o computador está ligado à tomada.", "Performance", false,
            $"$scheme=&{{{readScheme}}};if($scheme-eq'{highPerformance}'){{'true'}}else{{'false'}}",
            $"$output=powercfg.exe /setactive {highPerformance};if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}}",
            readScheme, RestoreCaptured,
            $"$output=powercfg.exe /setactive {balanced};if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}}");
    }

    private static Definition PowerSetting(
        string id, string name, string description, string category,
        string subgroup, string setting, uint appliedValue)
    {
        var read = $"$schemeOutput=powercfg.exe /getactivescheme;if($LASTEXITCODE-ne 0){{throw ($schemeOutput-join ' ')}};" +
                   "$schemeMatch=[regex]::Match(($schemeOutput-join ' '),'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}');if(-not$schemeMatch.Success){throw 'Plano de energia ativo não identificado.'};" +
                   $"$query=powercfg.exe /query $schemeMatch.Value {subgroup} {setting};if($LASTEXITCODE-ne 0){{throw ($query-join ' ')}};" +
                   "$hex=[regex]::Matches(($query-join ' '),'0x[0-9a-fA-F]+');if($hex.Count-lt 2){throw 'Valor do plano de energia não identificado.'};" +
                   "$ac=[Convert]::ToUInt32($hex[$hex.Count-2].Value.Substring(2),16);$dc=[Convert]::ToUInt32($hex[$hex.Count-1].Value.Substring(2),16);";
        var capture = read + "[pscustomobject]@{scheme=$schemeMatch.Value;ac=$ac;dc=$dc}|ConvertTo-Json -Compress";

        string RestoreCaptured(string baseline)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(baseline));
            return $"$state=([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))|ConvertFrom-Json);" +
                   "[guid]$scheme=[string]$state.scheme;" +
                   $"$output=powercfg.exe /setacvalueindex $scheme {subgroup} {setting} ([uint32]$state.ac);if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}};" +
                   $"$output=powercfg.exe /setdcvalueindex $scheme {subgroup} {setting} ([uint32]$state.dc);if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}};" +
                   "$output=powercfg.exe /setactive $scheme;if($LASTEXITCODE-ne 0){throw ($output-join ' ')}";
        }

        var apply = $"$output=powercfg.exe /setacvalueindex scheme_current {subgroup} {setting} {appliedValue};if($LASTEXITCODE-ne 0){{throw ($output-join ' ')}};" +
                    "$output=powercfg.exe /setactive scheme_current;if($LASTEXITCODE-ne 0){throw ($output-join ' ')}";
        var test = read + $"if($ac-eq{appliedValue}){{'true'}}else{{'false'}}";
        return new(id, name, description, category, false, test, apply, capture, RestoreCaptured,
            "throw 'O estado original deste ajuste de energia não está disponível.'");
    }

    private static Definition? FindDefinition(string id) =>
        Definitions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static OptimizationItem ToInactiveItem(Definition item) =>
        new(item.Id, item.Name, item.Description, item.Category, item.Restart, false);

    private static string QuotePowerShell(string value) => $"'{value.Replace("'", "''")}'";

    private static bool SnapshotsMatch(string expected, string actual)
    {
        return expected.Trim().Equals(actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyError(Exception error)
    {
        var message = error.Message.Trim();
        if (message.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("acesso", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            return "O Windows negou a alteração. Abra o HeatTurbo como administrador e tente novamente.";
        return string.IsNullOrWhiteSpace(message) ? "O Windows não concluiu a alteração." : message;
    }
}
