using HeatTurbo.Desktop;
using HeatTurbo.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace HeatTurbo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, "Local\\HeatTurbo.Desktop", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("O HeatTurbo já está aberto.", "HeatTurbo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var apiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
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
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    var authorized = context.Request.Headers.TryGetValue("X-HeatTurbo-Token", out var tokenHeader)
                        && tokenHeader.Count == 1
                        && FixedTimeTokenEquals(tokenHeader[0], apiToken);
                    if (!authorized)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { message = "Requisição local não autorizada." });
                        return;
                    }
                }
                await next();
            });
            app.MapRazorPages();
            MapApi(app);

            app.StartAsync().GetAwaiter().GetResult();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.FirstOrDefault()
                ?? throw new InvalidOperationException("Não foi possível iniciar o motor local do HeatTurbo.");
            var driverService = app.Services.GetRequiredService<DriverService>();

            ApplicationConfiguration.Initialize();
            using (var window = new HeatTurboWindow(
                new Uri(address), apiToken, () => driverService.IsInstallationRunning))
                Application.Run(window);

            app.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"O HeatTurbo não conseguiu iniciar.\n\n{ex.Message}",
                "Falha ao iniciar o HeatTurbo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }

    private static bool FixedTimeTokenEquals(string? candidate, string expected)
    {
        if (candidate is null || candidate.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(candidate),
            System.Text.Encoding.ASCII.GetBytes(expected));
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
        app.MapPost("/api/profiles/cs2/apply", async (OptimizationService service, CancellationToken ct) =>
        {
            var result = await service.ApplyCs2ProfileAsync(ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapGet("/api/profiles", (OptimizationService service) => Results.Ok(service.GetProfiles()));
        app.MapPost("/api/profiles/{id}/apply", async (string id, OptimizationService service, CancellationToken ct) =>
        {
            var result = await service.ApplyProfileAsync(id, ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/optimizations/restore-all", async (OptimizationService service, CancellationToken ct) =>
        {
            var result = await service.RestoreAllAsync(ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/backups", async (RestorePointService service, CancellationToken ct) => Results.Ok(await service.GetAllAsync(ct)));
        app.MapPost("/api/backups", async (RestorePointService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync("HeatTurbo - backup manual", ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/backups/{sequenceNumber:int}/restore", async (
            int sequenceNumber,
            RestorePointRestoreRequest request,
            RestorePointService service,
            CancellationToken ct) =>
        {
            var result = await service.RestoreAsync(sequenceNumber, request.Confirmation, ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapGet("/api/bios", async (BiosService service, CancellationToken ct) => Results.Ok(await service.AnalyzeAsync(ct)));
        app.MapGet("/api/drivers", async (bool? refresh, DriverService service, CancellationToken ct) =>
            Results.Ok(await service.ScanAsync(ct, refresh ?? false)));
        app.MapPost("/api/drivers/install", (
            DriverInstallRequest request,
            DriverService service) =>
        {
            var result = service.StartInstall(request);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapGet("/api/drivers/install/status", (DriverService service) =>
        {
            var operation = service.GetInstallOperation();
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapGet("/api/telemetry", async (TelemetryService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
        app.MapGet("/api/tools", (SystemToolsService service) => Results.Ok(service.Status()));
        app.MapPost("/api/tools/startup/{enabled:bool}", (bool enabled, SystemToolsService service) =>
        {
            var result = service.SetStartup(enabled);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/tools/auto-clean/{enabled:bool}", (bool enabled, SystemToolsService service) =>
        {
            var result = service.SetAutoClean(enabled);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/tools/clean", async (SystemToolsService service, CancellationToken ct) =>
        {
            var result = await service.CleanAsync(ct);
            return result.Success ? (IResult)Results.Ok(result) : Results.BadRequest(result);
        });
    }
}
