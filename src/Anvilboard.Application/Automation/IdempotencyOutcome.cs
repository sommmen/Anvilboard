namespace Anvilboard.Application.Automation;

/// <summary>Outcome of <see cref="IIdempotencyService.TryBeginAsync"/> for a caller-supplied
/// <c>Idempotency-Key</c> (see docs/features/agent-and-automation-surface.md, "Idempotency
/// Enforcement").</summary>
public enum IdempotencyOutcome
{
    /// <summary>No record exists for this key yet; the caller should proceed to execute the use
    /// case and then call <see cref="IIdempotencyService.CommitAsync"/>.</summary>
    New,

    /// <summary>A record already exists with the same canonical request hash; the mutation must
    /// not be re-executed and the caller should return the record's stored result (AC-007).
    /// </summary>
    ReplayOriginal,

    /// <summary>A record exists for this key but with a different canonical request hash (a
    /// different payload or actor); the caller must reject the request as
    /// <c>IDEMPOTENCY_KEY_REUSED</c> (409) without performing any mutation (AC-008).</summary>
    KeyReusedWithDifferentPayload,
}
