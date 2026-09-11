using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Lets a plugin announce that something happened which the UI may want to reflect, without that
/// plugin knowing anything about transports, connections, or workspaces' live clients.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> <see cref="IIssueHook"/>. A lifecycle hook participates in the
/// mutation it observes — it runs in that flow and can slow it down or fail it. An event published
/// here is fire-and-forget notification only: the host never awaits a client, never blocks the
/// publishing plugin, and never invokes lifecycle hooks in response (AC-RT-006).
/// </remarks>
public interface IPluginEventPublisher
{
    /// <summary>
    /// Publishes one plugin event. Implementations never throw for a delivery problem: a plugin
    /// must not fail its own work because no one was listening.
    /// </summary>
    void Publish(PluginEvent pluginEvent);
}

/// <summary>
/// The default for a host with no realtime pipeline — the one-shot CLI agent and tests. Keeping one
/// registered means a webhook route has a single code path whether or not realtime is enabled.
/// </summary>
public sealed class NullPluginEventPublisher : IPluginEventPublisher
{
    public void Publish(PluginEvent pluginEvent)
    {
    }
}

/// <summary>
/// A typed plugin event identified by a stable, namespaced <paramref name="EventType"/> such as
/// <c>github.pull_request.merged</c>.
/// </summary>
/// <remarks>
/// The event carries identity only — an event type and, optionally, the issue it concerns. It
/// deliberately has no free-form payload: anything a client renders is re-fetched through the
/// normal authorized REST queries, so a plugin can never push arbitrary or unauthorized content
/// into a browser.
/// </remarks>
/// <param name="WorkspaceId">The only workspace allowed to observe this event.</param>
/// <param name="EventType">Stable, namespaced event identifier.</param>
/// <param name="IssueId">The issue the event concerns, when it concerns one.</param>
/// <param name="OccurredAt">When the underlying event happened.</param>
public sealed record PluginEvent(
    WorkspaceId WorkspaceId,
    string EventType,
    IssueId? IssueId = null,
    DateTimeOffset? OccurredAt = null);
