using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.Configure<DefenderOptions>(builder.Configuration.GetSection(DefenderOptions.SectionName));
builder.Services.AddSingleton<DefenderStateStore>();
builder.Services.AddSingleton<AcDefenderService>();
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddHostedService<AcDefenderWorker>();
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/api/status", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));

app.MapPost("/api/target/generate", (DefenderStateStore store) =>
{
    var snapshot = store.GenerateTarget();
    return Results.Ok(snapshot);
});

app.MapPost("/api/target", (TargetTemperatureRequest request, DefenderStateStore store) =>
{
    var snapshot = store.SetTarget(request.TemperatureCelsius);
    return Results.Ok(snapshot);
});

app.MapPost("/api/defender", (DefenderEnabledRequest request, DefenderStateStore store) =>
{
    var snapshot = store.SetDefenderEnabled(request.Enabled);
    return Results.Ok(snapshot);
});

app.MapPost("/api/thermostat/refresh", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        await defender.RefreshRealThermostatAsync(cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.MapPost("/api/thermostat/force-target", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        await defender.ForceTargetAsync(cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.MapPost("/api/thermostat/force-boost", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        await defender.ForceCoolingBoostAsync(cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.Run();
