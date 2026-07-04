using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Wake-Up Truce watcher: polls the configured bedroom door sensor and, on a closed→open
/// transition inside the dawn window, tells the store to adopt the truce temperature — the person
/// gets their warm morning before they ever reach the thermostat. Born from the Jun 23 / Jul 2 dawn
/// battles that ended with the thermostat detached from the wall.
/// </summary>
public class WakeTruceService : BackgroundService
{
    private readonly DefenderOptions options;
    private readonly HomeAssistantClient client;
    private readonly DefenderStateStore store;
    private readonly ILogger<WakeTruceService> logger;

    private string? lastDoorState;

    public WakeTruceService(IOptions<DefenderOptions> options, HomeAssistantClient client, DefenderStateStore store, ILogger<WakeTruceService> logger)
    {
        this.options = options.Value;
        this.client = client;
        this.store = store;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.WakeTruceDoorSensorEntityId))
        {
            logger.LogInformation("Wake-up truce is disabled (no door sensor configured).");
            return;
        }

        logger.LogInformation("Wake-up truce watching {Entity} between {Start} and {End} (truce {Target:0.0} C for {Hold} min).",
            options.WakeTruceDoorSensorEntityId, options.WakeTruceWindowStart, options.WakeTruceWindowEnd,
            options.WakeTruceTargetCelsius, options.WakeTruceHoldMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reading = await client.GetMediaPlayerStateAsync(options.WakeTruceDoorSensorEntityId, stoppingToken);
                if (reading is not null)
                {
                    var doorState = reading.Value.State;
                    var opened = string.Equals(lastDoorState, "off", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(doorState, "on", StringComparison.OrdinalIgnoreCase);
                    if (opened && InDawnWindow(DateTimeOffset.Now))
                    {
                        store.BeginWakeTruce(options.WakeTruceTargetCelsius, options.WakeTruceHoldMinutes, DateTimeOffset.UtcNow);
                    }

                    lastDoorState = doorState;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wake-up truce poll failed; retrying");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool InDawnWindow(DateTimeOffset localNow)
    {
        if (!TimeOnly.TryParse(options.WakeTruceWindowStart, out var start)
            || !TimeOnly.TryParse(options.WakeTruceWindowEnd, out var end))
        {
            return false;
        }

        var t = TimeOnly.FromDateTime(localNow.LocalDateTime);
        return start <= end ? t >= start && t < end : t >= start || t < end;
    }
}
