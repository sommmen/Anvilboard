using Anvilboard.Application.Auditing;
using Anvilboard.Domain;

namespace Anvilboard.Application.Authorization;

/// <summary>Records security-sensitive authorization decisions without exposing credentials.</summary>
public interface IAuditService
{
    Task RecordAuthorizationDecisionAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        string action,
        string outcome,
        string correlationId,
        CancellationToken ct = default);

    Task RecordAsync(AuditEventRequest request, CancellationToken ct = default);
}

internal sealed class NoOpAuditService : IAuditService
{
    public Task RecordAuthorizationDecisionAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        string action,
        string outcome,
        string correlationId,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordAsync(AuditEventRequest request, CancellationToken ct = default) => Task.CompletedTask;
}
