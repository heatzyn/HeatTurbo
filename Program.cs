using HeatTurbo.Desktop;
using HeatTurbo.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Windows.Forms;

namespace HeatTurbo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton<SystemInfoService>();
        builder.Services.AddSingleton<OptimizationService>();
        builder.Services.AddSingleton<RestorePointService>();
        builder.Services.AddSingleton<BiosService>();
        builder.Services.AddSingleton<DriverService>();
        builder.Services.AddSingleton<TelemetryService>();
        builder.Services.AddSingleton<SystemToolsService>();

        using var app = builder.Build();
        if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
        app.UseStaticFiles();
        app.UseRouting();
        app.MapRazorPages();
        MapApi(app);

        app.StartAsync().GetAwaiter().GetResult();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("Não foi possível iniciar o motor local do HeatTurbo.");

        ApplicationConfiguration.Initialize();
        using (var window = new HeatTurboWindow(new Uri(address)))
            Application.Run(window);

        app.StopAsync().GetAwaiter().GetResult();
    }

    private static void MapApi(WebApplication app)
    {
        app.MapGet("/api/system", async (bool? refresh, SystemInfoService service, CancellationToken ct) =>
            Results.Ok(await service.GetSnapshotAsync(ct, refresh ?? false)));

        app.MapGet("/api/optimizations", async (OptimizationService service, CancellationToken ct) =>
            Results.Ok(await service.GetAllAsync(ct)));

        app.MapPost("/api/analyze", async (SystemInfoService systemService, OptimizationService optimizations, CancellationToken ct) =>
        {
            var system = await systemService.GetSnapshotAsync(ct, refresh: true);
            var items = await optimizations.GetAllAsync(ct);
            var active = items.Count(x => x.IsActive);
            var score = items.Count == 0 ? 100 : (int)Math.Round(active * 100d / items.Count);
            return Results.Ok(new { score, active, available = items.Count, system });
        });

        app.MapPost("/api/optimizations/{id}/apply", async (string id, OptimizationService service, CancellationToken ct) =>
        {
            var result = await service.ApplyAsync(id, ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapPost("/api/optimizations/{id}/restore", async (string id, OptimizationService service, CancellationToken ct) =>
        {
            var result = await service.RestoreAsync(id, ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/backups", async (RestorePointService service, CancellationToken ct) => Results.Ok(await service.GetAllAsync(ct)));
        app.MapPost("/api/backups", async (RestorePointService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync("HeatTurbo - backup manual", ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapGet("/api/bios", async (BiosService service, CancellationToken ct) => Results.Ok(await service.AnalyzeAsync(ct)));
        app.MapGet("/api/drivers", async (DriverService service, CancellationToken ct) => Results.Ok(await service.ScanAsync(ct)));
        app.MapPost("/api/drivers/install", async (DriverService service, CancellationToken ct) =>
        {
            var result = await service.InstallFromWindowsUpdateAsync(ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapGet("/api/telemetry", async (TelemetryService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
        app.MapGet("/api/tools", (SystemToolsService service) => Results.Ok(service.Status()));
        app.MapPost("/api/tools/startup/{enabled:bool}", (bool enabled, SystemToolsService service) => Results.Ok(service.SetStartup(enabled)));
        app.MapPost("/api/tools/auto-clean/{enabled:bool}", (bool enabled, SystemToolsService service) => Results.Ok(service.SetAutoClean(enabled)));
        app.MapPost("/api/tools/clean", async (SystemToolsService service, CancellationToken ct) => Results.Ok(await service.CleanAsync(ct)));
    }
}
