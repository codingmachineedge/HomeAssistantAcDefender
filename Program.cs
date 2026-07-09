using System.Security.Claims;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Components;
using HomeAssistantAcDefender.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

if (await CliCommands.TryRunAsync(args, builder.Configuration))
{
    return;
}

builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.Configure<DefenderOptions>(builder.Configuration.GetSection(DefenderOptions.SectionName));
builder.Services.Configure<KioskOptions>(builder.Configuration.GetSection(KioskOptions.SectionName));
builder.Services.AddSingleton<SettingsGitRepository>();
builder.Services.AddSingleton<WikiContentService>();
builder.Services.AddSingleton<DefenderStateStore>();
builder.Services.AddSingleton<AcDefenderService>();
builder.Services.AddSingleton<TwoFactorAuth>();
builder.Services.AddSingleton<GoogleDeviceLogin>();
builder.Services.AddSingleton<SdmCameraService>();
builder.Services.AddSingleton<HomeAssistantTokenStore>();
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddHostedService<AcDefenderWorker>();
builder.Services.AddHostedService<HubKioskService>();
builder.Services.AddHostedService<WakeTruceService>();
builder.Services.AddScoped<DefenderStateProvider>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMudServices();

// Persist the DataProtection key ring under App_Data (a docker volume in production) so container
// rebuilds do NOT invalidate auth cookies and antiforgery tokens. Without this every redeploy
// signed everyone out and left open tabs with dead circuits — "the site stopped working".
var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dataprotection-keys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("HomeAssistantAcDefender")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

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

// Serve the Site Wiki's images (docs/wiki/images/**) at /wikimedia. Rendered wiki HTML rewrites
// "images/..." paths here. Same public content as GitHub Pages, so no auth gate is needed.
var wikiImages = Path.Combine(app.Environment.ContentRootPath, "docs", "wiki", "images");
if (Directory.Exists(wikiImages))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wikiImages),
        RequestPath = WikiContentService.ImagesRequestPath,
    });
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Redirect("/login");
});

// Every /api/* endpoint requires an authenticated session. The Blazor UI reads state
// server-side via DefenderStateProvider (not these endpoints), so locking them down does
// not affect the dashboard. It does stop an unauthenticated visitor from reading the whole
// defender state (which names every stealth/camouflage guard) or controlling the thermostat.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/status", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));
api.MapGet("/settings", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));
api.MapGet("/usage/live", async (HomeAssistantClient homeAssistantClient, CancellationToken cancellationToken) =>
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
api.MapGet("/usage/alectra-hui", async (HomeAssistantClient homeAssistantClient, CancellationToken cancellationToken) =>
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
api.MapGet("/usage/history", async (
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

api.MapGet("/status/stream", async (HttpContext context, DefenderStateStore store, CancellationToken cancellationToken) =>
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

// Front-door camera: exchange the browser's WebRTC offer for Google's answer. Auth-gated with
// the rest of /api, so the stream is only reachable by signed-in household members.
api.MapPost("/camera/webrtc", async (CameraWebRtcRequest request, SdmCameraService camera) =>
{
    if (!camera.Enabled)
    {
        return Results.Json(new { error = "camera bridge is not configured" }, statusCode: 503);
    }

    var answer = await camera.GenerateWebRtcAnswerAsync(request.OfferSdp ?? "");
    return answer is null
        ? Results.Json(new { error = "stream negotiation failed" }, statusCode: 502)
        : Results.Json(new { answerSdp = answer });
});

api.MapPost("/target/generate", (DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("generate target", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.GenerateTarget();
    return Results.Ok(snapshot);
});

api.MapPost("/target", (TargetTemperatureRequest request, DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("set target", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.SetTarget(request.TemperatureCelsius);
    return Results.Ok(snapshot);
});

api.MapPost("/defender", async (DefenderEnabledRequest request, DefenderStateStore store, AcDefenderService defender) =>
{
    var gate = store.TryBeginWebsiteCommand(request.Enabled ? "turn defender on" : "pause defender", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.SetDefenderEnabled(request.Enabled);
    if (!request.Enabled)
    {
        try
        {
            await defender.ParkThermostatForStandDownAsync(CancellationToken.None);
            snapshot = store.GetSnapshot();
        }
        catch
        {
            // Parking is best-effort.
        }
    }

    return Results.Ok(snapshot);
});

api.MapPost("/settings", (SettingsRequest request, DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("save settings", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var snapshot = store.UpdateSettings(request);
    return Results.Ok(snapshot);
});

api.MapPost("/thermostat/refresh", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

api.MapPost("/thermostat/force-target", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

api.MapPost("/thermostat/force-boost", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

api.MapPost("/thermostat/fan", async (FanModeRequest request, AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

api.MapPost("/thermostat/off", async (AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

api.MapPost("/learn-history", async (AcDefenderService defender, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await defender.LearnFromHistoryAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/emergency", async (EmergencyProtocolRequest request, AcDefenderService defender, DefenderStateStore store, CancellationToken cancellationToken) =>
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

// Siesta (mess hall): start = thermostat-affecting (parks or turns the unit off once), so it
// takes the non-bypass debounce gate like /thermostat/off; cancel only clears a hold.
api.MapPost("/siesta", async (SiestaRequest request, DefenderStateStore store, AcDefenderService defender, CancellationToken cancellationToken) =>
{
    if (string.Equals(request.Action, "start", StringComparison.OrdinalIgnoreCase))
    {
        var gate = store.TryBeginWebsiteCommand("start siesta");
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!store.TryStartSiesta(request.Minutes ?? 60, "manual", out var startMessage))
        {
            return Results.BadRequest(new { error = startMessage });
        }

        try
        {
            await defender.ApplySiestaThermostatActionAsync(cancellationToken);
        }
        catch
        {
            // The park/off command is best-effort; the nap itself is already on.
        }

        return Results.Ok(store.GetSnapshot());
    }

    if (string.Equals(request.Action, "cancel", StringComparison.OrdinalIgnoreCase))
    {
        var gate = store.TryBeginWebsiteCommand("cancel siesta", bypassDebounce: true);
        if (!gate.Accepted)
        {
            return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
        }

        return store.TryCancelSiesta(out var cancelMessage)
            ? Results.Ok(store.GetSnapshot())
            : Results.BadRequest(new { error = cancelMessage });
    }

    return Results.BadRequest(new { error = "action must be start or cancel" });
});

// Reactor power: spend rations to summon the WinForge reactor's AI operator (1 ration = 1 h).
// Redemption stays auth-gated; it moves no money and touches no thermostat, so it bypasses the
// thermostat debounce.
api.MapPost("/reactor-power", (ReactorPowerRequest request, DefenderStateStore store) =>
{
    var gate = store.TryBeginWebsiteCommand("summon reactor operator", bypassDebounce: true);
    if (!gate.Accepted)
    {
        return Results.Json(gate.Snapshot, statusCode: StatusCodes.Status429TooManyRequests);
    }

    return store.TryRedeemReactorPower(request.Hours, out var message)
        ? Results.Ok(store.GetSnapshot())
        : Results.BadRequest(new { error = message });
});

// The PUBLIC reactor-power voucher for WinForge Web's poller: active flag + expiry only — no
// balances, no defender state. Deliberately outside the auth group (harmless LAN read) with an
// open CORS header so the Tauri/browser frontend can fetch it cross-origin.
app.MapGet("/api/reactor-power", (HttpContext context, DefenderStateStore store) =>
{
    context.Response.Headers.AccessControlAllowOrigin = "*";
    var (active, until) = store.GetReactorPowerVoucher();
    return Results.Ok(new
    {
        active,
        until,
        secondsRemaining = active && until is { } u ? (int)Math.Max(0, (u - DateTimeOffset.UtcNow).TotalSeconds) : 0,
        mode = "ai-operator",
    });
}).AllowAnonymous();

app.Run();
