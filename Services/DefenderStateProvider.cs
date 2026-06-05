using HomeAssistantAcDefender.Models;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Per-circuit live snapshot pump for the Blazor UI. Owns a single one-second
/// <see cref="PeriodicTimer"/>, polls the singleton <see cref="DefenderStateStore"/>, and raises
/// <see cref="Changed"/> so the layout and every page can re-render from one shared snapshot instead
/// of each running its own timer. Registered <c>Scoped</c> so the timer lives and dies with the
/// SignalR circuit.
/// </summary>
public sealed class DefenderStateProvider : IDisposable
{
    private readonly DefenderStateStore stateStore;
    private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource cancellation = new();

    public DefenderStateProvider(DefenderStateStore stateStore)
    {
        this.stateStore = stateStore;
        Snapshot = stateStore.GetSnapshot();
        _ = PumpAsync(cancellation.Token);
    }

    /// <summary>The most recent defender snapshot. Never null after construction.</summary>
    public DefenderSnapshot Snapshot { get; private set; }

    /// <summary>Raised once per second after <see cref="Snapshot"/> is refreshed.</summary>
    public event Action? Changed;

    /// <summary>Force an immediate refresh (used right after a UI action mutates state).</summary>
    public DefenderSnapshot Refresh()
    {
        Snapshot = stateStore.GetSnapshot();
        Changed?.Invoke();
        return Snapshot;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Snapshot = stateStore.GetSnapshot();
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        timer.Dispose();
        cancellation.Dispose();
    }
}
