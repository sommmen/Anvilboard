namespace Anvilboard.Application.Realtime;

/// <summary>
/// The post-commit handoff seam between a domain mutation and whatever transport happens to be
/// delivering presentation updates. Implementations must never block the caller on client delivery:
/// a committed mutation is already durable and its response must not wait for — or be failed by — a
/// slow, disconnected, or absent realtime consumer
/// (<c>docs/features/realtime-updates.md</c>, AC-RT-003).
/// </summary>
public interface IRealtimeUpdatePublisher
{
    /// <summary>
    /// Hands <paramref name="change"/> off for eventual delivery. Returns as soon as the change has
    /// been buffered, coalesced, or deliberately dropped by the configured policy; it never throws
    /// because of a transport-side problem.
    /// </summary>
    ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default);
}

/// <summary>
/// The default publisher for hosts with no realtime transport at all — the one-shot CLI agent, unit
/// tests, and any host that never calls <c>AddAnvilboardRealtime</c>. Keeping a no-op default
/// registered means <c>IssueService</c> has exactly one code path whether or not realtime is enabled.
/// </summary>
public sealed class NullRealtimeUpdatePublisher : IRealtimeUpdatePublisher
{
    public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
