using System.Text.Json;
using Anvilboard.Agent.Authorization;
using Anvilboard.Application.Authorization;
using Anvilboard.Application.Automation;

namespace Anvilboard.Agent.Automation;

/// <summary>
/// Wraps a mutating agent operation in the begin/commit idempotency protocol, so every mutation
/// gets duplicate suppression through one code path rather than thirteen hand-written ones.
/// </summary>
public sealed class AgentIdempotency(IIdempotencyService idempotency, AgentActorAccessor actors)
{
    private static readonly JsonSerializerOptions ReplayOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Executes <paramref name="mutation"/> at most once per
    /// (workspace, actor, operation, key) tuple.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A repeat of the same key with the same inputs returns the original stored result without
    /// re-executing (AC-007). A repeat with <em>different</em> inputs is a caller bug and is
    /// surfaced as <c>IDEMPOTENCY_KEY_REUSED</c>.
    /// </para>
    /// <para>
    /// Commit happens after the mutation succeeds, so a failed mutation leaves the key free for a
    /// genuine retry. The consequence is that a crash between the mutation committing and the
    /// idempotency record committing leaves the key unrecorded; that window is accepted because
    /// the alternative — reserving the key first — would permanently burn keys on transient
    /// failures, which is the far more frequent case.
    /// </para>
    /// </remarks>
    /// <param name="operationName">The agent operation name, part of the key's identity.</param>
    /// <param name="idempotencyKey">The caller-supplied key; validated before use.</param>
    /// <param name="requestInputs">
    /// The semantically significant inputs, hashed to detect key reuse with different payloads.
    /// </param>
    /// <param name="mutation">The write to perform when the key has not been seen.</param>
    public async Task<TResult> ExecuteAsync<TResult>(
        string operationName,
        string? idempotencyKey,
        object?[] requestInputs,
        Func<ActorContext, CancellationToken, Task<TResult>> mutation,
        CancellationToken ct = default)
    {
        var key = AgentRequestGuard.RequireIdempotencyKey(idempotencyKey);
        var actor = actors.Actor;
        var actorId = AgentActorId.For(actor);
        var requestHash = CanonicalRequestHash.Compute(operationName, requestInputs);

        var begin = await idempotency.TryBeginAsync(
            actor.WorkspaceId, actorId, operationName, key, requestHash, ct);

        switch (begin.Outcome)
        {
            case IdempotencyOutcome.ReplayOriginal:
                return Replay<TResult>(begin.StoredResultPayload);

            case IdempotencyOutcome.KeyReusedWithDifferentPayload:
                throw new AgentRequestException(
                    IdempotencyKeyReusedException.ErrorCode,
                    $"Idempotency key '{key}' was already used for '{operationName}' with different inputs.");
        }

        var result = await mutation(actor, ct);

        await idempotency.CommitAsync(
            actor.WorkspaceId, actorId, operationName, key, requestHash,
            JsonSerializer.Serialize(result, ReplayOptions),
            IdempotencyService.DefaultRetention,
            ct);

        return result;
    }

    private static TResult Replay<TResult>(string? storedPayload)
    {
        if (storedPayload is null)
        {
            throw new AgentRequestException(
                ErrorCodeCatalog.InternalError,
                "A replayed idempotent operation had no stored result payload.");
        }

        return JsonSerializer.Deserialize<TResult>(storedPayload, ReplayOptions)
            ?? throw new AgentRequestException(
                ErrorCodeCatalog.InternalError,
                "A replayed idempotent operation's stored result payload could not be deserialized.");
    }
}
