using Anvilboard.Domain;

namespace Anvilboard.Application.Automation;

/// <summary>
/// Enforces the <c>Idempotency-Key</c> contract for automation mutations (REST/CLI/MCP), per
/// docs/features/agent-and-automation-surface.md "Idempotency Enforcement" and tech-design
/// §10.1/§10.3. This service only detects and persists idempotency state; it never decides
/// authorization or workflow legality and never re-executes a use case itself — the caller
/// (the automation surface) is responsible for invoking the use case between
/// <see cref="TryBeginAsync"/> returning <see cref="IdempotencyOutcome.New"/> and calling
/// <see cref="CommitAsync"/>.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Looks up the composite key <c>(workspaceId, actorId, operation, idempotencyKey)</c>.
    /// Returns <see cref="IdempotencyOutcome.New"/> when no record exists yet;
    /// <see cref="IdempotencyOutcome.ReplayOriginal"/> (with the stored result payload) when a
    /// record exists with a matching <paramref name="canonicalRequestHash"/> (AC-007); or
    /// <see cref="IdempotencyOutcome.KeyReusedWithDifferentPayload"/> when a record exists with a
    /// different hash (AC-008).
    /// </summary>
    Task<IdempotencyBeginResult> TryBeginAsync(
        WorkspaceId workspaceId, string actorId, string operation, string idempotencyKey,
        string canonicalRequestHash, CancellationToken ct = default);

    /// <summary>
    /// Persists the idempotency record once the wrapped use case has committed successfully,
    /// setting <c>ExpiresAt = CreatedAt + retention</c> (the standard policy value is 30 days,
    /// see <see cref="IdempotencyService.DefaultRetention"/>).
    /// </summary>
    Task CommitAsync(
        WorkspaceId workspaceId, string actorId, string operation, string idempotencyKey,
        string canonicalRequestHash, string resultPayloadJson, TimeSpan retention,
        CancellationToken ct = default);
}
