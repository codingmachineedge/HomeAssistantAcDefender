using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class AcDefenderWorker : BackgroundService
{
    private readonly AcDefenderService defenderService;
    private readonly IOptionsMonitor<DefenderOptions> options;

    public AcDefenderWorker(AcDefenderService defenderService, IOptionsMonitor<DefenderOptions> options)
    {
        this.defenderService = defenderService;
        this.options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await defenderService.RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await defenderService.RunCycleAsync(stoppingToken);
        }
    }
}
