using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record BiosSnapshot(string Manufacturer, string Motherboard, string BiosVendor, string BiosVersion, string BiosDate, string Cpu, IReadOnlyList<BiosRecommendation> Recommendations);
public sealed record BiosRecommendation(string Name, string SuggestedValue, string Reason, string Risk);

public sealed class BiosService
{
    public async Task<BiosSnapshot> AnalyzeAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new("Disponível no Windows", "—", "—", "—", "—", "—", []);
        const string script = "$b=Get-CimInstance Win32_BIOS;$m=Get-CimInstance Win32_BaseBoard;$c=Get-CimInstance Win32_ComputerSystem;$p=Get-CimInstance Win32_Processor|Select-Object -First 1;[pscustomobject]@{manufacturer=$c.Manufacturer;board=($m.Manufacturer+' '+$m.Product);vendor=$b.Manufacturer;version=$b.SMBIOSBIOSVersion;date=$b.ReleaseDate;cpu=$p.Name}|ConvertTo-Json -Compress";
        using var doc = JsonDocument.Parse(await SystemInfoService.RunPowerShellAsync(script, ct));
        var r = doc.RootElement;
        var cpu = Text(r, "cpu");
        var recommendations = new List<BiosRecommendation>
        {
            new("Perfil da memória", cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "EXPO: Enabled" : "XMP: Enabled", "Permite que a RAM use o perfil de frequência certificado pelo fabricante.", "Teste estabilidade após ativar."),
            new("Resizable BAR", "Enabled", "Permite que a CPU acesse regiões maiores da memória da GPU.", "Requer GPU e boot UEFI compatíveis."),
            new("Above 4G Decoding", "Enabled", "Normalmente necessário para Resizable BAR.", "Não altere em instalações antigas com Legacy/CSM."),
            new("CSM / Legacy Boot", "Disabled", "Mantém inicialização UEFI moderna e compatibilidade com recursos atuais.", "Confirme que o Windows foi instalado em UEFI antes."),
            new("Virtualização", "Mantenha conforme seu uso", "Não há ganho consistente em desligar virtualização para CS2.", "Desativar quebra WSL, VMs e alguns recursos de segurança.")
        };
        return new(Text(r,"manufacturer"), Text(r,"board"), Text(r,"vendor"), Text(r,"version"), Text(r,"date"), cpu, recommendations);
    }
    private static string Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : "Não identificado";
}
