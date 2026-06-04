using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class AcDefenderWorker : BackgroundService
{
    private readonly DefenderStateStore stateStore;
    private readonly HomeAssistantClient homeAssistantClient;
    private readonly IOptionsMonitor<DefenderOptions> options;
    private readonly ILogger<AcDefenderWorker> logger;

    public AcDefenderWorker(
        DefenderStateStore stateStore,
        HomeAssistantClient homeAssistantClient,
        IOptionsMonitor<DefenderOptions> options,
        ILogger<AcDefenderWorker> logger)
    {
        this.stateStore = stateStore;
        this.homeAssistantClient = homeAssistantClient;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = stateStore.GetSnapshot();
            if (!snapshot.DefenderEnabled)
            {
                return;
            }

            if (!homeAssistantClient.IsConfigured)
            {
                stateStore.RecordHomeAssistantUnavailable("Home Assistant token is not configured; using dummy thermostat.");
                stateStore.ApplyDummyDefenderCycle();
                return;
            }

            var reading = await homeAssistantClient.GetDiningRoomClimateAsync(cancellationToken);
            if (reading is null)
            {
                stateStore.RecordHomeAssistantUnavailable("Dining room climate entity was not found; using dummy thermostat.");
                stateStore.ApplyDummyDefenderCycle();
                return;
            }

            snapshot = stateStore.RecordHomeAssistantReading(reading);
            var expectedSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction);
            var changed = Math.Abs(reading.SetPointCelsius - expectedSetPoint) > 0.05
                || !string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase);

            if (changed)
            {
                await homeAssistantClient.SetCoolingAsync(reading.EntityId, expectedSetPoint, cancellationToken);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} forced to {expectedSetPoint:0.0} C.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Defender cycle failed");
            stateStore.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
            stateStore.ApplyDummyDefenderCycle();
        }
    }
}
