using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Automation;

/// <inheritdoc cref="IIdempotencyService"/>
public sealed class IdempotencyService(AnvilboardDbContext dbContext) : IIdempotencyService
{
    /// <summary>The standard retention policy documented in tech-design §10.1: a committed
    /// idempotency record expires 30 days after <c>CreatedAt</c>. Callers may supply a different
    /// <see cref="TimeSpan"/> to <see cref="CommitAsync"/>, but should default to this value.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    public async Task<IdempotencyBeginResult> TryBeginAsync(
        WorkspaceId workspaceId, string actorId, string operation, string idempotencyKey,
        string canonicalRequestHash, CancellationToken ct = default)
    {
        var existing = await dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.WorkspaceId == workspaceId && r.ActorId == actorId
                     && r.Operation == operation && r.Key == idempotencyKey,
                ct);

        if (existing is null)
        {
            return IdempotencyBeginResult.New;
        }

        return existing.RequestHash == canonicalRequestHash
            ? IdempotencyBeginResult.ReplayOriginal(existing.ResultPayload)
            : IdempotencyBeginResult.KeyReusedWithDifferentPayload;
    }

    public async Task CommitAsync(
        WorkspaceId workspaceId, string actorId, string operation, string idempotencyKey,
        string canonicalRequestHash, string resultPayloadJson, TimeSpan retention,
        CancellationToken ct = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = IdempotencyRecordId.New(),
            WorkspaceId = workspaceId,
            ActorId = actorId,
            Operation = operation,
            Key = idempotencyKey,
            RequestHash = canonicalRequestHash,
            ResultPayload = resultPayloadJson,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + retention,
        });

        await dbContext.SaveChangesAsync(ct);
    }
}
