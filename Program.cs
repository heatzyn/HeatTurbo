using HeatTurbo.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<OptimizationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/api/system", async (bool? refresh, SystemInfoService service, CancellationToken ct) =>
    Results.Ok(await service.GetSnapshotAsync(ct, refresh ?? false)));

app.MapGet("/api/optimizations", async (OptimizationService service, CancellationToken ct) =>
    Results.Ok(await service.GetAllAsync(ct)));

app.MapPost("/api/analyze", async (SystemInfoService service, OptimizationService optimizations, CancellationToken ct) =>
{
    var system = await service.GetSnapshotAsync(ct, refresh: true);
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

app.Run();
