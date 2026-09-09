namespace Anvilboard.Application.Automation;

/// <summary>
/// Result of <see cref="IIdempotencyService.TryBeginAsync"/>. The spec's illustrative signature
/// returns a bare <see cref="IdempotencyOutcome"/>, but AC-007 requires a replay to return the
/// original result without re-executing the mutation, so this result additionally carries the
/// previously committed <see cref="StoredResultPayload"/> when <see cref="Outcome"/> is
/// <see cref="IdempotencyOutcome.ReplayOriginal"/> (<c>null</c> for the other two outcomes).
/// </summary>
public sealed record IdempotencyBeginResult(IdempotencyOutcome Outcome, string? StoredResultPayload)
{
    public static readonly IdempotencyBeginResult New = new(IdempotencyOutcome.New, StoredResultPayload: null);

    public static IdempotencyBeginResult ReplayOriginal(string storedResultPayload) =>
        new(IdempotencyOutcome.ReplayOriginal, storedResultPayload);

    public static readonly IdempotencyBeginResult KeyReusedWithDifferentPayload =
        new(IdempotencyOutcome.KeyReusedWithDifferentPayload, StoredResultPayload: null);
}
