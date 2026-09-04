using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Fire-and-forget lifecycle hook invoked after an <see cref="Issue"/> mutation is persisted, for
/// plugins that react to board activity rather than feed it — e.g. posting a Slack message when
/// an issue moves to "In Review", or pushing a status change back to the originating GitHub/Linear
/// item. Hooks run after the write has committed and cannot veto or mutate the change; a plugin
/// that needs to prevent or alter a write should not use this interface (there is currently no
/// synchronous/blocking hook, by design, so a slow or failing plugin can never block the UI).
/// </summary>
public interface IIssueHook : IAnvilboardPlugin
{
    /// <summary>
    /// Called once per persisted <see cref="ActivityEvent"/>. Implementations should filter on
    /// <see cref="IssueHookContext.Event"/>.<c>Type</c> for the transitions they care about and
    /// return quickly; the host invokes all registered hooks concurrently and logs (rather than
    /// propagates) exceptions so one misbehaving plugin cannot break issue mutations for others.
    /// </summary>
    Task OnIssueChangedAsync(IssueHookContext context, CancellationToken cancellationToken);
}

/// <summary>Snapshot of an issue and the activity event that just fired, passed to every hook.</summary>
public sealed record IssueHookContext(Issue Issue, ActivityEvent Event);
