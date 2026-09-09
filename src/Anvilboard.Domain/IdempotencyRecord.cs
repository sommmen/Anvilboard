namespace Anvilboard.Domain;

/// <summary>
/// A persisted record of a completed automation mutation, keyed by
/// <c>(WorkspaceId, ActorId, Operation, Key)</c>, that lets the automation surface detect a safe
/// replay (identical request) versus a conflicting reuse (same key, different payload) of a
/// caller-supplied <c>Idempotency-Key</c>. Persisted only after the wrapped use case commits
/// successfully (see docs/features/agent-and-automation-surface.md).
/// </summary>
public sealed class IdempotencyRecord
{
    public IdempotencyRecordId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Identity of the caller that performed the mutation; part of the lookup key so two
    /// different actors sharing the same idempotency key can never collide.</summary>
    public required string ActorId { get; init; }

    /// <summary>The logical operation name (e.g. a route or CLI/MCP operation id) the key is
    /// scoped to.</summary>
    public required string Operation { get; init; }

    /// <summary>The caller-supplied <c>Idempotency-Key</c> value, opaque, 1-255 chars.</summary>
    public required string Key { get; init; }

    /// <summary>SHA-256 hash of the canonical (stable property order) request payload; used to
    /// distinguish a safe replay from a conflicting reuse of the same key.</summary>
    public required string RequestHash { get; init; }

    /// <summary>The serialized result of the committed use case, replayed verbatim on a matching
    /// retry instead of re-executing the mutation.</summary>
    public required string ResultPayload { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Retention horizon; the standard policy value is 30 days from
    /// <see cref="CreatedAt"/>, but the exact value is caller-supplied
    /// (see <c>IIdempotencyService.CommitAsync</c>).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
