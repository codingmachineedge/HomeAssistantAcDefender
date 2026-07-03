using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Energy kiosk worker: keeps the neutral "Energy" Lovelace dashboard cast to a Google display
/// 24/7 and feeds it unbranded sensors (budget, usage, costs). Rules, per the household:
///  - The kiosk NEVER mentions the defender — entity ids and names are plain energy terms.
///  - If something else takes over the screen (a timer, someone casting), back off for a
///    cooldown (default 30 min) before reclaiming it. A dead/idle screen is reclaimed at once —
///    the cooldown is a courtesy to humans, not to crashes.
///  - When casting, set the volume to 0 first so the Google connect chime is silent, then restore
///    the previous volume a few seconds after the view is up.
///  - The kiosk's "Show this &amp; last month's usage" button is watched here: a press flips the
///    review sensor on for a few minutes (the dashboard shows the two-month cards conditionally)
///    and is recorded in the defender as a cost-concern signal.
/// </summary>
public class HubKioskService : BackgroundService
{
    private readonly KioskOptions options;
    private readonly HomeAssistantClient client;
    private readonly DefenderStateStore store;
    private readonly ILogger<HubKioskService> logger;

    private bool kioskWasShowing;
    private DateTimeOffset? interruptedAt;
    private DateTimeOffset? reviewUntil;
    private string? lastButtonPressState;

    public HubKioskService(IOptions<KioskOptions> options, HomeAssistantClient client, DefenderStateStore store, ILogger<HubKioskService> logger)
    {
        this.options = options.Value;
        this.client = client;
        this.store = store;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.MediaPlayerEntity))
        {
            logger.LogInformation("Energy kiosk is disabled (no media player configured).");
            return;
        }

        logger.LogInformation("Energy kiosk running for {Entity} -> {Dashboard}/{View}",
            options.MediaPlayerEntity, options.DashboardPath, options.ViewPath);

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.CheckIntervalSeconds, 15, 600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await PushKioskSensorsAsync(now, stoppingToken);
                await WatchUsageButtonAsync(now, stoppingToken);
                await EnsureCastAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Energy kiosk cycle failed; retrying next interval");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Casts the kiosk when the screen is free, honoring the human-interrupt cooldown.</summary>
    private async Task EnsureCastAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var media = await client.GetMediaPlayerStateAsync(options.MediaPlayerEntity, cancellationToken);
        if (media is null)
        {
            kioskWasShowing = false;
            return; // display unreachable; try again next cycle
        }

        var (state, appName, volume) = media.Value;
        var showingLovelace = string.Equals(appName, "Home Assistant Lovelace", StringComparison.OrdinalIgnoreCase)
            && state is "playing" or "paused" or "idle";

        if (showingLovelace)
        {
            kioskWasShowing = true;
            interruptedAt = null;
            return;
        }

        var somethingElseActive = state is "playing" or "paused"
            || (!string.IsNullOrEmpty(appName) && !string.Equals(appName, "Backdrop", StringComparison.OrdinalIgnoreCase));

        if (somethingElseActive)
        {
            // A human (or their timer/cast) owns the screen. Note the takeover once and stand down.
            if (kioskWasShowing || interruptedAt is null)
            {
                interruptedAt = now;
                logger.LogInformation("Energy kiosk interrupted by {App}/{State}; waiting {Minutes} min.",
                    appName ?? "?", state, options.InterruptCooldownMinutes);
            }

            kioskWasShowing = false;
            return;
        }

        // Screen is free (off/idle/unavailable-app). Respect the cooldown only after a real takeover.
        if (interruptedAt is { } t && now - t < TimeSpan.FromMinutes(Math.Max(0, options.InterruptCooldownMinutes)))
        {
            return;
        }

        interruptedAt = null;

        // Chime-free start: volume 0 -> cast -> wait -> restore.
        var restoreVolume = volume is > 0 ? volume.Value : options.DefaultRestoreVolume;
        await client.CallHomeAssistantServiceAsync("media_player", "volume_set",
            new { entity_id = options.MediaPlayerEntity, volume_level = 0.0 }, cancellationToken);
        await client.CallHomeAssistantServiceAsync("cast", "show_lovelace_view",
            new { entity_id = options.MediaPlayerEntity, dashboard_path = options.DashboardPath, view_path = options.ViewPath },
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.VolumeRestoreDelaySeconds, 2, 60)), cancellationToken);
        await client.CallHomeAssistantServiceAsync("media_player", "volume_set",
            new { entity_id = options.MediaPlayerEntity, volume_level = Math.Clamp(restoreVolume, 0.0, 1.0) }, cancellationToken);

        kioskWasShowing = true;
        logger.LogInformation("Energy kiosk cast to {Entity} (volume restored to {Volume:0.00}).",
            options.MediaPlayerEntity, restoreVolume);
    }

    /// <summary>Publishes the unbranded kiosk sensors (state-machine entities, re-posted every cycle).</summary>
    private async Task PushKioskSensorsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = store.GetSnapshot();
        var runtime = snapshot.AcRuntime;
        var budget = snapshot.ElectricityBudget;
        if (runtime is null)
        {
            return; // runtime counters not initialized yet; sensors next cycle
        }

        // This month + last month from the per-day ledger (calendar months, local time).
        var local = now.ToLocalTime();
        var thisMonthKey = local.ToString("yyyy-MM");
        var lastMonthKey = local.AddMonths(-1).ToString("yyyy-MM");
        double thisMonthHours = 0, thisMonthCost = 0, lastMonthHours = 0, lastMonthCost = 0;
        foreach (var day in store.GetAcDailyUsage())
        {
            if (day.Date.StartsWith(thisMonthKey, StringComparison.Ordinal))
            {
                thisMonthHours += day.Hours;
                thisMonthCost += day.CostDollars;
            }
            else if (day.Date.StartsWith(lastMonthKey, StringComparison.Ordinal))
            {
                lastMonthHours += day.Hours;
                lastMonthCost += day.CostDollars;
            }
        }

        var reviewOn = reviewUntil is { } r && now < r;

        async Task Push(string suffix, string value, string name, string? unit = null, string icon = "mdi:flash")
            => await client.PushSensorStateAsync($"sensor.energy_kiosk_{suffix}", value,
                unit is null
                    ? new { friendly_name = name, icon }
                    : (object)new { friendly_name = name, unit_of_measurement = unit, icon },
                cancellationToken);

        await Push("ac_hours_today", runtime.TodayHours.ToString("0.0"), "AC hours today", "h", "mdi:clock-outline");
        await Push("ac_cost_today", runtime.EstimatedCostTodayDollars.ToString("0.00"), "Cost today", "$", "mdi:currency-usd");
        await Push("ac_hours_month", thisMonthHours.ToString("0.0"), "AC hours this month", "h", "mdi:calendar-month");
        await Push("ac_cost_month", thisMonthCost.ToString("0.00"), "Cost this month", "$", "mdi:currency-usd");
        await Push("ac_hours_last_month", lastMonthHours.ToString("0.0"), "AC hours last month", "h", "mdi:calendar-arrow-left");
        await Push("ac_cost_last_month", lastMonthCost.ToString("0.00"), "Cost last month", "$", "mdi:currency-usd");
        await Push("ac_cost_lifetime", runtime.EstimatedCostLifetimeDollars.ToString("0.00"), "Cost all-time", "$", "mdi:sigma");

        if (budget is not null && budget.Enabled)
        {
            await Push("budget_monthly", budget.MonthlyBudgetCad.ToString("0"), "Monthly budget", "$", "mdi:piggy-bank-outline");
            await Push("budget_spent", budget.MonthToDateAllInCad.ToString("0.00"), "Spent so far", "$", "mdi:cash");
            await Push("budget_projected", budget.ProjectedMonthEndCad.ToString("0.00"), "Projected month-end", "$", "mdi:chart-line");
            await Push("budget_status", budget.OverBudget ? "Over pace" : "On track", "Budget", null,
                budget.OverBudget ? "mdi:alert-circle-outline" : "mdi:check-circle-outline");
        }
        else
        {
            await Push("budget_status", "No budget set", "Budget", null, "mdi:piggy-bank-outline");
        }

        await client.PushSensorStateAsync("sensor.energy_kiosk_review", reviewOn ? "on" : "off",
            new { friendly_name = "Monthly review", icon = "mdi:calendar-search" }, cancellationToken);
    }

    /// <summary>Watches the kiosk's usage button; a press opens the review window and is recorded.</summary>
    private async Task WatchUsageButtonAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UsageButtonEntity))
        {
            return;
        }

        var media = await client.GetMediaPlayerStateAsync(options.UsageButtonEntity, cancellationToken);
        if (media is null)
        {
            return; // helper missing; dashboard setup creates it
        }

        var pressState = media.Value.State; // input_button state = timestamp of last press
        if (lastButtonPressState is not null
            && !string.Equals(pressState, lastButtonPressState, StringComparison.Ordinal)
            && !string.Equals(pressState, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            reviewUntil = now.AddMinutes(Math.Clamp(options.ReviewMinutes, 1, 120));
            store.RecordKioskCostConcern(now);
            logger.LogInformation("Kiosk usage button pressed; showing the two-month review until {Until:HH:mm:ss}.",
                reviewUntil.Value.ToLocalTime());
        }

        lastButtonPressState = pressState;
    }
}
