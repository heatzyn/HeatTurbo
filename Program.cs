using HeatTurbo.Desktop;
using HeatTurbo.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Net;
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
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 64 * 1024;
                options.Limits.MaxRequestHeaderCount = 32;
                options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            });
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
            app.Use(async (context, next) =>
            {
                ApplySecurityHeaders(context.Response.Headers);

                if (!IsTrustedLoopbackRequest(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.Headers.CacheControl = "no-store, max-age=0";
                    context.Response.Headers.Pragma = "no-cache";

                    if (!HasTrustedBrowserContext(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { message = "Origem local não autorizada." });
                        return;
                    }

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
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                    context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                }
            });
            app.UseRouting();
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

    private static bool IsTrustedLoopbackRequest(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        var localAddress = context.Connection.LocalIpAddress;
        if (remoteAddress is null || localAddress is null ||
            !IPAddress.IsLoopback(remoteAddress) || !IPAddress.IsLoopback(localAddress))
            return false;

        if (!IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var requestedAddress) ||
            !IPAddress.IsLoopback(requestedAddress))
            return false;

        return context.Request.Host.Port is null || context.Request.Host.Port == context.Connection.LocalPort;
    }

    private static bool HasTrustedBrowserContext(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite) &&
            fetchSite.Count == 1 &&
            !fetchSite[0].Equals("same-origin", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var headerName in new[] { "Origin", "Referer" })
        {
            if (!context.Request.Headers.TryGetValue(headerName, out var values) || values.Count == 0)
                continue;
            if (values.Count != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) ||
                !IPAddress.IsLoopback(address) || uri.Port != context.Connection.LocalPort)
                return false;
        }
        return true;
    }

    private static void ApplySecurityHeaders(IHeaderDictionary headers)
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; " +
            "frame-ancestors 'none'; form-action 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=(), serial=(), bluetooth=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
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
