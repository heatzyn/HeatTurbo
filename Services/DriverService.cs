using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record DriverInfo(string Device, string Manufacturer, string Version, string Date, string Kind, string OfficialUrl);

public sealed class DriverService
{
    public async Task<IReadOnlyList<DriverInfo>> ScanAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];
        const string script = "Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceClass -in @('DISPLAY','SYSTEM') -and $_.DriverVersion} | Sort-Object DeviceClass,DeviceName -Unique | Select-Object DeviceName,Manufacturer,DriverVersion,DriverDate,DeviceClass | ConvertTo-Json -Compress";
        var json = await SystemInfoService.RunPowerShellAsync(script, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        return rows.Take(30).Select(x =>
        {
            var name = Text(x, "DeviceName"); var maker = Text(x, "Manufacturer");
            return new DriverInfo(name, maker, Text(x,"DriverVersion"), Text(x,"DriverDate"), Text(x,"DeviceClass"), OfficialUrl(name + " " + maker));
        }).ToArray();
    }
    private static string OfficialUrl(string value) => value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "https://www.nvidia.com/en-us/drivers/" : value.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "https://www.amd.com/en/support/download/drivers.html" : value.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "https://www.intel.com/content/www/us/en/support/detect.html" : "ms-settings:windowsupdate-optionalupdates";
    private static string Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : "—";
}
