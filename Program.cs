using System.Security.Claims;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Components;
using HomeAssistantAcDefender.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
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
builder.Services.AddSingleton<TwoFactorAuth>();
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddHostedService<AcDefenderWorker>();
builder.Services.AddScoped<DefenderStateProvider>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMudServices();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "AC_Defender_Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>AC Defender error</title></head>"
            + "<body style=\"font-family:'Segoe UI',Arial,sans-serif;padding:48px;background:#0f1614;color:#e7f6ef\">"
            + "<h1>Something went wrong</h1>"
            + "<p>The web request failed, but the background defender keeps reading and protecting the thermostat.</p>"
            + "<p><a href=\"/\" style=\"color:#46c1a7\">Return to the dashboard</a></p>"
            + "</body></html>");
    }));
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Redirect("/login");
});

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
app.MapGet("/api/usage/alectra-hui", async (HomeAssistantClient homeAssistantClient, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await homeAssistantClient.GetAlectraHuiEntitiesAsync(cancellationToken));
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
    var gate = store.TryBeginWebsiteCommand(request.Enabled ? "turn defender on" : "pause defender", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.SetDefenderEnabled(request.Enabled);
    return Results.Ok(snapshot);
});

app.MapPost("/api/settings", (SettingsRequest request, DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("save settings", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.UpdateSettings(request);
    return Results.Ok(snapshot);
});

app.MapPost("/api/thermostat/refresh", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
{
    try
    {
        var gate = store.TryBeginWebsiteCommand("refresh thermostat", bypassDebounce: true);
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
        var emergencyChangesThermostat = string.Equals(request.Protocol, "too-cold", StringComparison.OrdinalIgnoreCase);
        var gate = store.TryBeginWebsiteCommand($"emergency {request.Protocol}", bypassDebounce: !emergencyChangesThermostat);
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
