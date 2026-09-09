namespace Anvilboard.Domain;

/// <summary>An immutable, workspace-scoped record of a security-sensitive action.</summary>
public sealed class AuditEvent
{
    public AuditEventId Id { get; init; }
    public WorkspaceId WorkspaceId { get; init; }
    public required string ActorId { get; init; }
    public AuditChannel Channel { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
    public required string CorrelationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string ResultSummary { get; init; }
}
