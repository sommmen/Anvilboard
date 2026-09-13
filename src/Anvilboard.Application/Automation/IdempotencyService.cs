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

        // Expired records are treated as absent rather than matched: without this the documented
        // 30-day retention has no observable effect, and a key would stay bound to its original
        // result forever even after the record became eligible for cleanup. The comparison is
        // deliberately in memory - the SQLite provider cannot translate a DateTimeOffset
        // comparison, and the composite index above already narrows the query to a single row.
        if (existing is null || existing.ExpiresAt <= DateTimeOffset.UtcNow)
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

        // An expired record is reported as absent by TryBeginAsync, so the caller re-executes and
        // commits again. The composite key is unique, so that second commit has to replace the
        // stale row rather than insert alongside it.
        var existing = await dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.WorkspaceId == workspaceId && r.ActorId == actorId
                     && r.Operation == operation && r.Key == idempotencyKey,
                ct);

        if (existing is not null)
        {
            dbContext.IdempotencyRecords.Remove(existing);
        }

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
