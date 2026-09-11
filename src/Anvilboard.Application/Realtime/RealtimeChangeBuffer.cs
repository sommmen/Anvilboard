namespace Anvilboard.Application.Realtime;

/// <summary>The outcome of offering a change to <see cref="RealtimeChangeBuffer"/>.</summary>
public enum RealtimeBufferResult
{
    /// <summary>The change was buffered under a previously unused coalescing key.</summary>
    Buffered,

    /// <summary>The change replaced, or was absorbed by, a pending change for the same key.</summary>
    Coalesced,

    /// <summary>The buffer held <see cref="RealtimeOptions.QueueCapacity"/> distinct keys already.</summary>
    Dropped,
}

/// <summary>
/// A bounded, latest-wins buffer of pending changes. Changes that describe the same thing — the same
/// issue, the same workspace dashboard — collapse onto a single coalescing key, so a burst of
/// mutations produces a bounded number of sends instead of one send per mutation (AC-RT-004).
/// </summary>
/// <remarks>
/// Capacity is counted in distinct keys rather than in raw changes on purpose: an issue that is
/// being hammered must never be able to exhaust the buffer, because each of its updates replaces the
/// previous pending entry. Only a genuinely new key can be dropped, and a drop is always accounted
/// for through <see cref="RealtimeMetrics.RecordDropped"/> by the caller.
/// </remarks>
public sealed class RealtimeChangeBuffer(int capacity)
{
    private readonly Lock gate = new();

    private readonly Dictionary<RealtimeCoalescingKey, RealtimeChange> pending = [];

    private readonly int capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Queue capacity must be positive.");

    public int Count
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    public RealtimeBufferResult Offer(RealtimeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var key = RealtimeCoalescingKey.For(change);
        lock (gate)
        {
            if (pending.TryGetValue(key, out var existing))
            {
                pending[key] = Merge(existing, change);
                return RealtimeBufferResult.Coalesced;
            }

            if (pending.Count >= capacity)
            {
                return RealtimeBufferResult.Dropped;
            }

            pending[key] = change;
            return RealtimeBufferResult.Buffered;
        }
    }

    /// <summary>Atomically removes and returns everything currently buffered.</summary>
    public IReadOnlyList<RealtimeChange> Drain()
    {
        lock (gate)
        {
            if (pending.Count == 0)
            {
                return [];
            }

            var drained = pending.Values.ToList();
            pending.Clear();
            return drained;
        }
    }

    /// <summary>
    /// Keeps whichever change carries the most information. For issues that means the highest
    /// version, and a creation is never downgraded to an update: a client that has not yet seen the
    /// issue still needs to be told the issue is new rather than silently patched.
    /// </summary>
    private static RealtimeChange Merge(RealtimeChange existing, RealtimeChange incoming)
    {
        if (existing is RealtimeIssueChange previousIssue && incoming is RealtimeIssueChange nextIssue)
        {
            var winner = nextIssue.Version >= previousIssue.Version ? nextIssue : previousIssue;
            return previousIssue.ChangeKind == RealtimeIssueChangeKind.Created
                ? winner with { ChangeKind = RealtimeIssueChangeKind.Created }
                : winner;
        }

        if (existing is RealtimeActivityChange previousActivity && incoming is RealtimeActivityChange nextActivity)
        {
            return nextActivity.OccurredAt >= previousActivity.OccurredAt ? nextActivity : previousActivity;
        }

        return incoming.OccurredAt >= existing.OccurredAt ? incoming : existing;
    }
}

/// <summary>
/// Identifies the "thing" a change is about. Two changes sharing a key are interchangeable from the
/// client's point of view, which is precisely what makes latest-wins coalescing safe.
/// </summary>
internal readonly record struct RealtimeCoalescingKey(Guid WorkspaceId, string EventType, string Subject)
{
    public static RealtimeCoalescingKey For(RealtimeChange change) => change switch
    {
        RealtimeIssueChange issue => new(issue.WorkspaceId.Value, issue.EventType, issue.IssueId.ToString()),
        RealtimeActivityChange activity => new(activity.WorkspaceId.Value, activity.EventType, activity.IssueId.ToString()),
        RealtimeDashboardChange dashboard => new(dashboard.WorkspaceId.Value, dashboard.EventType, string.Empty),
        RealtimePluginEventChange plugin => new(
            plugin.WorkspaceId.Value,
            plugin.EventType,
            $"{plugin.PluginEventType}:{plugin.IssueId?.ToString() ?? string.Empty}"),
        _ => new(change.WorkspaceId.Value, change.EventType, change.CorrelationId),
    };
}
