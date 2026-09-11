using Anvilboard.Domain;

namespace Anvilboard.Application.Realtime;

/// <summary>
/// A compact, workspace-scoped notification that something the UI renders has changed. A change is
/// only ever created <em>after</em> the mutation that caused it has committed, and it deliberately
/// carries identity + version rather than a full entity projection: the client re-fetches through
/// the normal REST queries, which stay authoritative
/// (see <c>docs/features/realtime-updates.md</c>, "Reconnect and gap recovery").
/// </summary>
/// <param name="WorkspaceId">The only workspace allowed to observe this change.</param>
/// <param name="EventType">Stable wire discriminator; clients re-fetch on an unknown value.</param>
/// <param name="CorrelationId">The correlation id of the mutation that produced the change.</param>
/// <param name="OccurredAt">When the originating mutation committed; used for publication latency.</param>
public abstract record RealtimeChange(
    WorkspaceId WorkspaceId,
    string EventType,
    string CorrelationId,
    DateTimeOffset OccurredAt);

/// <summary>The kind of issue mutation a <see cref="RealtimeIssueChange"/> describes.</summary>
public enum RealtimeIssueChangeKind
{
    /// <summary>The issue did not exist before the committed mutation.</summary>
    Created,

    /// <summary>An existing issue's fields or workflow state changed.</summary>
    Updated,
}

/// <summary>An issue was created or updated in the owning workspace.</summary>
public sealed record RealtimeIssueChange(
    WorkspaceId WorkspaceId,
    IssueId IssueId,
    long Version,
    RealtimeIssueChangeKind ChangeKind,
    string CorrelationId,
    DateTimeOffset OccurredAt)
    : RealtimeChange(WorkspaceId, "issue.changed", CorrelationId, OccurredAt);

/// <summary>An activity event was recorded against an issue in the owning workspace.</summary>
public sealed record RealtimeActivityChange(
    WorkspaceId WorkspaceId,
    IssueId IssueId,
    ActivityEventId ActivityEventId,
    long IssueVersion,
    string CorrelationId,
    DateTimeOffset OccurredAt)
    : RealtimeChange(WorkspaceId, "activity.added", CorrelationId, OccurredAt);

/// <summary>The dashboard summary for the owning workspace may no longer be current.</summary>
public sealed record RealtimeDashboardChange(
    WorkspaceId WorkspaceId,
    string SummaryVersion,
    string CorrelationId,
    DateTimeOffset OccurredAt)
    : RealtimeChange(WorkspaceId, "dashboard.changed", CorrelationId, OccurredAt);

/// <summary>
/// An approved, UI-eligible typed plugin event relayed to a workspace. Kept deliberately opaque
/// (event type + optional issue identity) so a plugin can never push an arbitrary payload at a
/// browser (<c>docs/features/realtime-updates.md</c>, AC-RT-006).
/// </summary>
public sealed record RealtimePluginEventChange(
    WorkspaceId WorkspaceId,
    string PluginEventType,
    IssueId? IssueId,
    string CorrelationId,
    DateTimeOffset OccurredAt)
    : RealtimeChange(WorkspaceId, "plugin.event", CorrelationId, OccurredAt);
