using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Components;
using HomeAssistantAcDefender.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

if (await CliCommands.TryRunAsync(args, builder.Configuration))
{
    return;
}

builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.Configure<DefenderOptions>(builder.Configuration.GetSection(DefenderOptions.SectionName));
builder.Services.AddSingleton<DefenderStateStore>();
builder.Services.AddSingleton<AcDefenderService>();
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddHostedService<AcDefenderWorker>();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/api/status", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));
app.MapGet("/api/settings", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));
app.MapGet("/api/usage/live", async (HomeAssistantClient homeAssistantClient, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await homeAssistantClient.GetLiveUsageAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapGet("/api/usage/history", async (
    string? entityId,
    DateTimeOffset? from,
    DateTimeOffset? to,
    double? hours,
    HomeAssistantClient homeAssistantClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-Math.Max(0.1, hours ?? 24));
        return Results.Ok(await homeAssistantClient.GetUsageHistoryAsync(entityId, start, end, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

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
    var gate = store.TryBeginWebsiteCommand("generate target");
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.GenerateTarget();
    return Results.Ok(snapshot);
});

app.MapPost("/api/target", (TargetTemperatureRequest request, DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("set target");
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

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
        var gate = store.TryBeginWebsiteCommand("refresh thermostat");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

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
        var gate = store.TryBeginWebsiteCommand("force exact target");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

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
        var gate = store.TryBeginWebsiteCommand("force cooling");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

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
        var gate = store.TryBeginWebsiteCommand($"set fan to {request.FanMode}");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

        await defender.ForceFanModeAsync(request.FanMode, cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.MapPost("/api/thermostat/off", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        var gate = store.TryBeginWebsiteCommand("turn thermostat off");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

        await defender.TurnThermostatOffAsync(cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.MapPost("/api/emergency", async (EmergencyProtocolRequest request, AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        var gate = store.TryBeginWebsiteCommand($"emergency {request.Protocol}", bypassDebounce: true);
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

        await defender.ApplyEmergencyProtocolAsync(request.Protocol, cancellationToken);
        return Results.Ok(store.GetSnapshot());
    }
    catch (Exception ex)
    {
        store.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        return Results.BadRequest(store.GetSnapshot());
    }
});

app.Run();
