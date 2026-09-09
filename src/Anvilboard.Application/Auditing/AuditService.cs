using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Application.Auditing;

public sealed class AuditService(AnvilboardDbContext db) : IAuditService
{
    public Task RecordAuthorizationDecisionAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        string action,
        string outcome,
        string correlationId,
        CancellationToken ct = default) =>
        RecordAsync(new AuditEventRequest(
            workspaceId,
            $"member:{actor.MemberId}",
            AuditChannel.System,
            "authorization.decision",
            "authorization",
            action,
            correlationId,
            outcome), ct);

    public async Task RecordAsync(AuditEventRequest request, CancellationToken ct = default)
    {
        var auditEvent = new AuditEvent
        {
            Id = AuditEventId.New(),
            WorkspaceId = request.WorkspaceId,
            ActorId = request.ActorId,
            Channel = request.Channel,
            Action = request.Action,
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            CorrelationId = request.CorrelationId,
            OccurredAt = DateTimeOffset.UtcNow,
            ResultSummary = SecretRedactor.Scrub(request.ResultSummary),
        };

        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }
}
