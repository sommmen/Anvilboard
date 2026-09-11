using Anvilboard.Application.Realtime;

namespace Anvilboard.Api.Realtime;

/// <summary>
/// The wire shape clients receive. Deliberately flat and additive-only: a client that does not
/// recognise <see cref="EventType"/> falls back to a normal re-fetch instead of breaking, which is
/// what lets the envelope gain fields without a coordinated client release.
/// </summary>
/// <remarks>
/// The workspace id is intentionally absent. A connection is only ever in its own workspace group,
/// so repeating the id would add nothing a client can act on while giving a compromised client one
/// more identifier to probe with.
/// </remarks>
public sealed record RealtimeChangeEnvelope
{
    public required string EventType { get; init; }

    public required string CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Set for <c>issue.changed</c> and <c>activity.added</c>; may be set for plugin events.</summary>
    public Guid? IssueId { get; init; }

    /// <summary>The issue version this change reflects; clients use it to detect a gap.</summary>
    public long? Version { get; init; }

    /// <summary><c>CREATED</c> or <c>UPDATED</c> for <c>issue.changed</c>.</summary>
    public string? ChangeKind { get; init; }

    public Guid? ActivityEventId { get; init; }

    public string? SummaryVersion { get; init; }

    public string? PluginEventType { get; init; }

    public static RealtimeChangeEnvelope From(RealtimeChange change) => change switch
    {
        RealtimeIssueChange issue => Base(issue) with
        {
            IssueId = issue.IssueId.Value,
            Version = issue.Version,
            ChangeKind = issue.ChangeKind == RealtimeIssueChangeKind.Created ? "CREATED" : "UPDATED",
        },
        RealtimeActivityChange activity => Base(activity) with
        {
            IssueId = activity.IssueId.Value,
            Version = activity.IssueVersion,
            ActivityEventId = activity.ActivityEventId.Value,
        },
        RealtimeDashboardChange dashboard => Base(dashboard) with
        {
            SummaryVersion = dashboard.SummaryVersion,
        },
        RealtimePluginEventChange plugin => Base(plugin) with
        {
            IssueId = plugin.IssueId?.Value,
            PluginEventType = plugin.PluginEventType,
        },
        _ => Base(change),
    };

    private static RealtimeChangeEnvelope Base(RealtimeChange change) => new()
    {
        EventType = change.EventType,
        CorrelationId = change.CorrelationId,
        OccurredAt = change.OccurredAt,
    };
}
