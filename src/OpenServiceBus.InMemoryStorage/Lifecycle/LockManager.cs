using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Lifecycle;

/// <summary>
/// Periodically sweeps the store for expired peek-locks and abandons them, returning their
/// messages to the available pool. Each tick is a single store-wide call
/// (<see cref="IMessageStore.ExpireLocksAll"/>) so the sweep cost does not grow with the
/// number of entities, and the timer backs off to <see cref="IdleSweepInterval"/> while the
/// store holds no locks at all (see docs/Background-Sweepers.md). Tests can drive expiration
/// directly via <see cref="IMessageStore.ExpireLocks"/> with a FakeTimeProvider - this
/// background loop exists for runtime, not for tests.
/// </summary>
public sealed class LockManager : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(1);

    private readonly IMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LockManager> _logger;

    public LockManager(
        IMessageStore store,
        TimeProvider timeProvider,
        ILogger<LockManager> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var busy = SweepOnce();
                var next = busy
                    ? SweepInterval
                    : TimeSpan.FromTicks(Math.Min(timer.Period.Ticks * 2, IdleSweepInterval.Ticks));
                if (next != timer.Period) timer.Period = next;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private bool SweepOnce()
    {
        var now = _timeProvider.GetUtcNow();
        try
        {
            var sweep = _store.ExpireLocksAll(now);
            foreach (var (queueName, expired) in sweep.ByQueue)
            {
                _logger.LogDebug("Expired {Count} lock(s) on queue {Queue}", expired, queueName);
            }
            return sweep.ByQueue.Count > 0 || sweep.HasPending;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lock expiration sweep failed");
            return true;
        }
    }
}
