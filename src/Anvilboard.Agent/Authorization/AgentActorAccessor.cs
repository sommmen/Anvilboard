using Anvilboard.Application.Authorization;

namespace Anvilboard.Agent.Authorization;

/// <summary>
/// Carries the <see cref="ActorContext"/> resolved by <see cref="WorkspaceAuthorizationPolicy"/>
/// from the policy to the operation method, within one invocation scope. The CLI/MCP counterpart
/// of the REST host's <c>HttpContext.Items[ActorContextItemsKey]</c>.
/// </summary>
/// <remarks>
/// Scoped to the invocation, so it is impossible for one invocation to observe another's actor.
/// </remarks>
public sealed class AgentActorAccessor
{
    private ActorContext? _actor;

    /// <summary>
    /// The authenticated actor for the invocation in flight.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The policy did not run. Throwing (rather than returning <c>null</c>) is deliberate: if the
    /// policy were ever removed or reordered, every operation fails loudly instead of silently
    /// reverting to the unauthenticated behaviour this component exists to eliminate.
    /// </exception>
    public ActorContext Actor =>
        _actor ?? throw new InvalidOperationException(
            "No ActorContext was resolved for this invocation; "
            + $"{nameof(WorkspaceAuthorizationPolicy)} must run before the operation.");

    /// <summary>Whether an actor has been resolved for this invocation.</summary>
    public bool HasActor => _actor is not null;

    internal void Set(ActorContext actor) => _actor = actor;
}

/// <summary>
/// Formats an <see cref="ActorContext"/> as the credential-qualified actor id recorded on audit,
/// activity, and idempotency rows.
/// </summary>
/// <remarks>
/// Including the token id makes an automation action attributable to the specific credential that
/// performed it, so revoking one token identifies exactly what it did. It also scopes idempotency
/// keys per credential: two tokens may reuse a key value without colliding.
/// </remarks>
public static class AgentActorId
{
    public static string For(ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.ApiTokenId is { } token
            ? $"member:{actor.MemberId.Value}+token:{token.Value}"
            : $"member:{actor.MemberId.Value}";
    }
}
