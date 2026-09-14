using Anvilboard.Domain;

namespace Anvilboard.Application.Auditing;

/// <summary>
/// Read-only, workspace-scoped access to the append-only audit trail written by
/// <see cref="Authorization.IAuditService"/>. Deliberately a separate interface (rather than a
/// <c>QueryAsync</c> member on <see cref="Authorization.IAuditService"/>): that service's
/// <c>NoOpAuditService</c> implementation exists to silently discard writes when auditing is
/// disabled, and a read has no honest no-op form — it must either fake a result or throw, both of
/// which are dishonest for an audit trail. See plan `DR-AUD-001`.
/// </summary>
public interface IAuditQueryService
{
    Task<AuditQueryResult> QueryAsync(
        WorkspaceId workspaceId, AuditQuery query, CancellationToken ct = default);
}

/// <summary>
/// Filters for an audit history read. <see cref="WorkspaceId"/> is supplied separately by the
/// caller (from <c>RestWorkspaceScope</c>/<c>AgentWorkspaceScope</c>), never as a member here — the
/// workspace is never a caller-supplied input on this path (BR1).
/// </summary>
public sealed record AuditQuery(
    string? ActorId = null,
    DateTimeOffset? OccurredAfter = null,
    DateTimeOffset? OccurredBefore = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Action = null,
    AuditChannel? Channel = null,
    int? Limit = null,
    string? Cursor = null);

public sealed record AuditEventDto(
    Guid Id,
    string ActorId,
    AuditChannel Channel,
    string Action,
    string TargetType,
    string TargetId,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string ResultSummary);

/// <summary>
/// A page of audit history. <see cref="AppliedQuery"/> echoes the normalized query back (notably
/// the resolved <see cref="AuditQuery.Limit"/>) so a caller can see which defaults were applied
/// without guessing, mirroring <c>BoardQueryResult</c>.
/// </summary>
public sealed record AuditQueryResult(
    IReadOnlyList<AuditEventDto> Events,
    bool HasMore,
    string? NextCursor,
    AuditQuery AppliedQuery);

public sealed class AuditQueryException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
