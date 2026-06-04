using System.Text.Json;
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
app.MapGet("/api/settings", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));

app.MapGet("/api/status/stream", async (HttpContext context, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    while (!cancellationToken.IsCancellationRequested)
    {
        var json = JsonSerializer.Serialize(store.GetSnapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
});

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

app.MapPost("/api/settings", (SettingsRequest request, DefenderStateStore store) =>
{
    var snapshot = store.UpdateSettings(request);
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

app.MapPost("/api/thermostat/fan", async (FanModeRequest request, AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        await defender.ForceFanModeAsync(request.FanMode, cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.Run();
