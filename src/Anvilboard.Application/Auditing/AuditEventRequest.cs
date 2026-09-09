using Anvilboard.Domain;

namespace Anvilboard.Application.Auditing;

public sealed record AuditEventRequest(
    WorkspaceId WorkspaceId,
    string ActorId,
    AuditChannel Channel,
    string Action,
    string TargetType,
    string TargetId,
    string CorrelationId,
    string ResultSummary);
