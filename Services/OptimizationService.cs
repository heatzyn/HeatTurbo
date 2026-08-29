using System.Runtime.InteropServices;

namespace HeatTurbo.Services;

public sealed record OptimizationItem(string Id, string Name, string Description, string Category, bool RequiresRestart, bool IsActive);
public sealed record ActionResult(bool Success, string Message, string? Id = null);

public sealed class OptimizationService
{
    private readonly RestorePointService _restorePoints;
    public OptimizationService(RestorePointService restorePoints) => _restorePoints = restorePoints;
    private sealed record Definition(string Id, string Name, string Description, string Category, bool Restart, string Test, string Apply, string Restore);

    private static readonly Definition[] Definitions =
    [
        new("game-mode", "Modo de Jogo do Windows", "Prioriza recursos para o jogo em primeiro plano.", "Gaming", false,
            "$v=(Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\GameBar' -Name AutoGameModeEnabled -ErrorAction SilentlyContinue).AutoGameModeEnabled; if($v -eq 1){'true'}else{'false'}",
            "New-Item -Path 'HKCU:\\Software\\Microsoft\\GameBar' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\GameBar' -Name AutoGameModeEnabled -Type DWord -Value 1",
            "Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\GameBar' -Name AutoGameModeEnabled -ErrorAction SilentlyContinue"),
        new("game-dvr", "Desativar gravação em segundo plano", "Evita que o Game DVR capture partidas sem você pedir.", "Gaming", false,
            "$v=(Get-ItemProperty -Path 'HKCU:\\System\\GameConfigStore' -Name GameDVR_Enabled -ErrorAction SilentlyContinue).GameDVR_Enabled; if($v -eq 0){'true'}else{'false'}",
            "New-Item -Path 'HKCU:\\System\\GameConfigStore' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\System\\GameConfigStore' -Name GameDVR_Enabled -Type DWord -Value 0",
            "Remove-ItemProperty -Path 'HKCU:\\System\\GameConfigStore' -Name GameDVR_Enabled -ErrorAction SilentlyContinue"),
        new("mouse-acceleration", "Desativar aceleração do mouse", "Mantém o movimento da mira consistente no CS2.", "Latência", false,
            "$p=Get-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse'; if($p.MouseSpeed -eq '0' -and $p.MouseThreshold1 -eq '0' -and $p.MouseThreshold2 -eq '0'){'true'}else{'false'}",
            "Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseSpeed -Value '0'; Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseThreshold1 -Value '0'; Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseThreshold2 -Value '0'",
            "Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseSpeed -Value '1'; Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseThreshold1 -Value '6'; Set-ItemProperty -Path 'HKCU:\\Control Panel\\Mouse' -Name MouseThreshold2 -Value '10'"),
        new("high-performance", "Plano de energia de alto desempenho", "Reduz economia agressiva de energia durante a partida.", "Performance", false,
            "$s=(powercfg /getactivescheme); if($s -match '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'){'true'}else{'false'}",
            "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e"),
        new("hags", "Agendamento de GPU por hardware (HAGS)", "Permite testar o scheduler de GPU do Windows em drivers WDDM modernos.", "Gaming", true,
            "$v=(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode;if($v -eq 2){'true'}else{'false'}",
            "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers' -Name HwSchMode -Type DWord -Value 2",
            "Remove-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue"),
        new("background-apps", "Limitar apps da Store em segundo plano", "Reduz atividade de aplicativos UWP que você não está usando durante o jogo.", "Performance", false,
            "$v=(Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications' -Name GlobalUserDisabled -ErrorAction SilentlyContinue).GlobalUserDisabled;if($v -eq 1){'true'}else{'false'}",
            "New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications' -Force|Out-Null;Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications' -Name GlobalUserDisabled -Type DWord -Value 1",
            "Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications' -Name GlobalUserDisabled -ErrorAction SilentlyContinue"),
        new("visual-effects", "Priorizar desempenho visual do Windows", "Reduz animações e efeitos da interface para liberar recursos do desktop.", "Performance", false,
            "$v=(Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects' -Name VisualFXSetting -ErrorAction SilentlyContinue).VisualFXSetting;if($v -eq 2){'true'}else{'false'}",
            "New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects' -Force|Out-Null;Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects' -Name VisualFXSetting -Type DWord -Value 2",
            "Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects' -Name VisualFXSetting -ErrorAction SilentlyContinue"),
        new("power-throttling", "Desativar Power Throttling", "Evita que o Windows coloque processos relacionados ao jogo em modo de economia.", "Latência", true,
            "$v=(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling' -Name PowerThrottlingOff -ErrorAction SilentlyContinue).PowerThrottlingOff;if($v -eq 1){'true'}else{'false'}",
            "New-Item -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling' -Force|Out-Null;Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling' -Name PowerThrottlingOff -Type DWord -Value 1",
            "Remove-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling' -Name PowerThrottlingOff -ErrorAction SilentlyContinue"),
        new("xbox-overlay", "Desativar Xbox Game Bar", "Remove a sobreposição e atalhos da Game Bar durante as partidas.", "Gaming", false,
            "$v=(Get-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' -Name UseNexusForGameBarEnabled -ErrorAction SilentlyContinue).UseNexusForGameBarEnabled;if($v -eq 0){'true'}else{'false'}",
            "Set-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' -Name UseNexusForGameBarEnabled -Type DWord -Value 0",
            "Remove-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' -Name UseNexusForGameBarEnabled -ErrorAction SilentlyContinue"),
        new("transparency", "Desativar transparência do Windows", "Reduz composição visual no desktop sem afetar o jogo.", "Interface", false,
            "$v=(Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize' -Name EnableTransparency -ErrorAction SilentlyContinue).EnableTransparency;if($v -eq 0){'true'}else{'false'}",
            "Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize' -Name EnableTransparency -Type DWord -Value 0",
            "Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize' -Name EnableTransparency -Type DWord -Value 1"),
        new("menu-delay", "Reduzir atraso dos menus", "Deixa menus e interface do Windows mais responsivos.", "Interface", false,
            "$v=(Get-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name MenuShowDelay -ErrorAction SilentlyContinue).MenuShowDelay;if([int]$v -le 20){'true'}else{'false'}",
            "Set-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name MenuShowDelay -Value '10'",
            "Set-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name MenuShowDelay -Value '400'"),
        new("startup-delay", "Remover atraso dos apps de inicialização", "Inicia aplicativos autorizados assim que a sessão abre.", "Inicialização", false,
            "$v=(Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Serialize' -Name StartupDelayInMSec -ErrorAction SilentlyContinue).StartupDelayInMSec;if($v -eq 0){'true'}else{'false'}",
            "New-Item 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Serialize' -Force|Out-Null;Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Serialize' -Name StartupDelayInMSec -Type DWord -Value 0",
            "Remove-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Serialize' -Name StartupDelayInMSec -ErrorAction SilentlyContinue"),
        new("mmcss", "Perfil multimídia responsivo", "Reserva menos CPU para tarefas em segundo plano durante cargas multimídia.", "Latência", true,
            "$v=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name SystemResponsiveness).SystemResponsiveness;if($v -eq 10){'true'}else{'false'}",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name SystemResponsiveness -Type DWord -Value 10",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name SystemResponsiveness -Type DWord -Value 20"),
        new("games-task", "Prioridade MMCSS para jogos", "Configura a tarefa Games do agendador multimídia para alta prioridade.", "Gaming", true,
            "$v=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name Priority).Priority;if($v -eq 6){'true'}else{'false'}",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name Priority -Type DWord -Value 6;Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name 'Scheduling Category' -Value 'High';Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name 'SFIO Priority' -Value 'High'",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name Priority -Type DWord -Value 2;Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name 'Scheduling Category' -Value 'Medium';Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games' -Name 'SFIO Priority' -Value 'Normal'"),
        new("network-throttle", "Remover throttling multimídia de rede", "Evita limitação periódica do processamento de pacotes multimídia.", "Rede", true,
            "$v=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name NetworkThrottlingIndex).NetworkThrottlingIndex;if($v -eq 4294967295){'true'}else{'false'}",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name NetworkThrottlingIndex -Type DWord -Value 4294967295",
            "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name NetworkThrottlingIndex -Type DWord -Value 10"),
        new("delivery-optimization", "Desativar compartilhamento de updates", "Impede uploads P2P do Windows Update em segundo plano.", "Rede", false,
            "$v=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization' -Name DODownloadMode -ErrorAction SilentlyContinue).DODownloadMode;if($v -eq 0){'true'}else{'false'}",
            "New-Item 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization' -Force|Out-Null;Set-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization' -Name DODownloadMode -Type DWord -Value 0",
            "Remove-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization' -Name DODownloadMode -ErrorAction SilentlyContinue"),
        new("windows-suggestions", "Desativar sugestões do Windows", "Reduz conteúdo sugerido e tarefas promocionais em segundo plano.", "Geral", false,
            "$v=(Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Name SubscribedContent-338388Enabled -ErrorAction SilentlyContinue).'SubscribedContent-338388Enabled';if($v -eq 0){'true'}else{'false'}",
            "Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Name 'SubscribedContent-338388Enabled' -Type DWord -Value 0;Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Name 'SoftLandingEnabled' -Type DWord -Value 0",
            "Remove-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Name 'SubscribedContent-338388Enabled','SoftLandingEnabled' -ErrorAction SilentlyContinue"),
        new("usb-suspend", "Desativar suspensão seletiva USB", "Evita economia de energia agressiva em mouse, teclado e headset USB.", "Latência", false,
            "$v=powercfg /query scheme_current sub_usb usbselective;if(($v|Select-String 'Current AC Power Setting Index: 0x00000000')){'true'}else{'false'}",
            "powercfg /setacvalueindex scheme_current sub_usb usbselective 0;powercfg /setactive scheme_current",
            "powercfg /setacvalueindex scheme_current sub_usb usbselective 1;powercfg /setactive scheme_current"),
        new("pcie-link-state", "Desativar economia PCI Express", "Evita latência de saída do estado de economia no link PCIe.", "Latência", false,
            "$v=powercfg /query scheme_current sub_pciexpress aspm;if(($v|Select-String 'Current AC Power Setting Index: 0x00000000')){'true'}else{'false'}",
            "powercfg /setacvalueindex scheme_current sub_pciexpress aspm 0;powercfg /setactive scheme_current",
            "powercfg /setacvalueindex scheme_current sub_pciexpress aspm 2;powercfg /setactive scheme_current"),
        new("core-parking", "Reduzir estacionamento de núcleos", "Mantém mais núcleos disponíveis no plano de energia durante jogos.", "Performance", false,
            "$v=powercfg /query scheme_current sub_processor cpmincores;if(($v|Select-String 'Current AC Power Setting Index: 0x00000064')){'true'}else{'false'}",
            "powercfg /setacvalueindex scheme_current sub_processor cpmincores 100;powercfg /setactive scheme_current",
            "powercfg /setacvalueindex scheme_current sub_processor cpmincores 10;powercfg /setactive scheme_current")
    ];

    public async Task<IReadOnlyList<OptimizationItem>> GetAllAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Definitions.Select(x => new OptimizationItem(x.Id, x.Name, x.Description, x.Category, x.Restart, false)).ToArray();

        var result = new List<OptimizationItem>();
        foreach (var item in Definitions)
        {
            var active = (await SystemInfoService.RunPowerShellAsync(item.Test, ct)).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            result.Add(new(item.Id, item.Name, item.Description, item.Category, item.Restart, active));
        }
        return result;
    }

    public async Task<ActionResult> ApplyAsync(string id, CancellationToken ct)
    {
        var backup = await _restorePoints.EnsureBeforeChangeAsync(ct);
        if (!backup.Success) return backup with { Id = id };
        return await ExecuteAsync(id, true, ct);
    }
    public Task<ActionResult> RestoreAsync(string id, CancellationToken ct) => ExecuteAsync(id, false, ct);

    private static async Task<ActionResult> ExecuteAsync(string id, bool apply, CancellationToken ct)
    {
        var item = Definitions.FirstOrDefault(x => x.Id == id);
        if (item is null) return new(false, "Ajuste não encontrado.", id);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new(false, "Os ajustes só podem ser aplicados no Windows.", id);
        try
        {
            await SystemInfoService.RunPowerShellAsync(apply ? item.Apply : item.Restore, ct);
            return new(true, apply ? $"{item.Name} ativado." : $"{item.Name} restaurado.", id);
        }
        catch (Exception ex) { return new(false, ex.Message, id); }
    }
}
