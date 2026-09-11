using Microsoft.Extensions.Logging;

namespace Anvilboard.Application.Realtime;

/// <summary>
/// The publisher used whenever realtime is enabled. It does nothing except account for the change
/// and drop it into the shared bounded buffer, so the mutation that called it returns immediately
/// regardless of how many clients are connected or how slow they are (AC-RT-003).
/// </summary>
public sealed class CoalescingRealtimeUpdatePublisher(
    RealtimeChangeBuffer buffer,
    RealtimeDispatchSignal signal,
    RealtimeMetrics metrics,
    ILogger<CoalescingRealtimeUpdatePublisher> logger) : IRealtimeUpdatePublisher
{
    public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        metrics.RecordPublished(change.EventType);

        var result = buffer.Offer(change);
        switch (result)
        {
            case RealtimeBufferResult.Coalesced:
                metrics.RecordCoalesced(change.EventType);
                break;
            case RealtimeBufferResult.Dropped:
                metrics.RecordDropped(change.EventType);
                // A dropped presentation notification is not a data-loss event: the client re-fetches
                // authoritative state, so this is logged as a capacity signal rather than an error.
                logger.LogWarning(
                    "Realtime buffer saturated; dropped {EventType} for workspace {WorkspaceId} (correlation {CorrelationId}).",
                    change.EventType,
                    change.WorkspaceId,
                    change.CorrelationId);
                return ValueTask.CompletedTask;
        }

        signal.Signal();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Wakes <see cref="RealtimeDispatcher"/> when work arrives, so an idle host does not poll. Level-
/// triggered on purpose: a signal raised while the dispatcher is draining is not lost, it simply
/// causes the next debounce cycle to start immediately.
/// </summary>
public sealed class RealtimeDispatchSignal : IDisposable
{
    private readonly SemaphoreSlim available = new(0, 1);

    public void Signal()
    {
        if (available.CurrentCount == 0)
        {
            try
            {
                available.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another publisher released concurrently; the dispatcher is already awake.
            }
        }
    }

    public Task WaitAsync(CancellationToken ct) => available.WaitAsync(ct);

    public void Dispose() => available.Dispose();
}
