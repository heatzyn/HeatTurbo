using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record BiosSnapshot(string Manufacturer, string Motherboard, string BiosVendor, string BiosVersion, string BiosDate, string Cpu, string Gpu, string FirmwareMode, IReadOnlyList<BiosRecommendation> Recommendations);
public sealed record BiosRecommendation(string Name, string SuggestedValue, string Reason, string Risk);

public sealed class BiosService
{
    public async Task<BiosSnapshot> AnalyzeAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new("Disponível no Windows", "—", "—", "—", "—", "—", "—", "—", []);
        const string script = "$b=Get-CimInstance Win32_BIOS;$m=Get-CimInstance Win32_BaseBoard;$c=Get-CimInstance Win32_ComputerSystem;$p=Get-CimInstance Win32_Processor|Select-Object -First 1;$g=Get-CimInstance Win32_VideoController|Where-Object {$_.Name -notmatch 'Basic|Remote'}|Select-Object -First 1;$f=(Get-ComputerInfo -Property BiosFirmwareType).BiosFirmwareType;[pscustomobject]@{manufacturer=$c.Manufacturer;board=($m.Manufacturer+' '+$m.Product);vendor=$b.Manufacturer;version=$b.SMBIOSBIOSVersion;date=$b.ReleaseDate;cpu=$p.Name;gpu=$g.Name;firmware=$f}|ConvertTo-Json -Compress";
        using var doc = JsonDocument.Parse(await SystemInfoService.RunPowerShellAsync(script, ct));
        var r = doc.RootElement;
        var cpu = Text(r, "cpu");
        var gpu = Text(r,"gpu"); var board = Text(r,"board"); var firmware = Text(r,"firmware");
        var modernGpu = gpu.Contains("RTX",StringComparison.OrdinalIgnoreCase) || gpu.Contains("RX ",StringComparison.OrdinalIgnoreCase) || gpu.Contains("Arc",StringComparison.OrdinalIgnoreCase);
        var consumerBoard = new[] {"ASUS","MSI","GIGABYTE","ASROCK"}.Any(x => board.Contains(x,StringComparison.OrdinalIgnoreCase));
        var recommendations = new List<BiosRecommendation>();
        if (consumerBoard) recommendations.Add(new("Perfil da memória", cpu.Contains("AMD",StringComparison.OrdinalIgnoreCase) ? "EXPO: Enabled" : "XMP: Enabled", "Usa o perfil de frequência certificado pelo fabricante da RAM.", "Faça teste de memória; volte para Auto se houver instabilidade."));
        if (modernGpu && firmware.Contains("Uefi",StringComparison.OrdinalIgnoreCase))
        {
            recommendations.Add(new("Above 4G Decoding", "Enabled", "Pré-requisito comum para Resizable BAR em GPUs modernas.", "Mantenha CSM desativado."));
            recommendations.Add(new("Resizable BAR", "Enabled", "Pode melhorar acesso da CPU à memória da GPU em jogos compatíveis.", "O efeito varia por jogo e driver."));
        }
        if (consumerBoard && cpu.Contains("AMD",StringComparison.OrdinalIgnoreCase))
        {
            recommendations.Add(new("CPPC / Preferred Cores", "Enabled", "Ajuda o Windows a escolher os melhores núcleos Ryzen para cargas leves e jogos.", "Não altera tensão."));
            recommendations.Add(new("Precision Boost Overdrive", "Auto ou Enabled", "Permite que o boost use limites validados pela placa e refrigeração.", "Monitore temperatura; não use limites manuais nesta fase."));
        }
        recommendations.Add(new("Fast Boot", "Opcional", "Reduz o tempo de POST, sem alterar FPS.", "Desative temporariamente se precisar acessar a BIOS com frequência."));
        recommendations.Add(new("Virtualização", "Mantenha conforme seu uso", "Desligar virtualização não garante ganho no CS2.", "Pode quebrar WSL, VMs e recursos de segurança."));
        return new(Text(r,"manufacturer"), board, Text(r,"vendor"), Text(r,"version"), Text(r,"date"), cpu, gpu, firmware, recommendations);
    }
    private static string Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : "Não identificado";
}
