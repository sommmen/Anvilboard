using Anvilboard.Application.Automation;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Anvilboard.Application.Realtime;

/// <summary>Host-only capability for publishing events after it has authenticated workspace routing.</summary>
/// <remarks>
/// Extends <see cref="IHostOnlyPluginCapability"/> purely as a marker: it carries no members of its
/// own, but it is what lets <c>Anvilboard.Infrastructure</c>'s plugin construction filter this
/// capability (and <see cref="PluginEventRelay"/>, which implements it) out of the provider handed
/// to <c>ActivatorUtilities.CreateInstance</c> for reflection-loaded plugins, without that project
/// ever referencing this type directly.
/// </remarks>
public interface ITrustedPluginEventPublisher : IHostOnlyPluginCapability
{
    void Publish(PluginEvent pluginEvent);
}

/// <summary>
/// Maps plugin events onto the same coalescing realtime pipeline committed mutations use, so a
/// plugin gets live delivery without a second transport, a second security model, or any ability to
/// reach a client another way.
/// </summary>
/// <remarks>
/// The host owns this capability; third-party plugins receive the no-op public publisher and therefore
/// cannot select another workspace. Two further properties keep host-relayed events safe:
/// <list type="bullet">
/// <item>Only event types on <see cref="RealtimeOptions.RelayedPluginEventTypes"/> are relayed. An
/// unapproved event is dropped silently, so adding an event to a plugin cannot, by itself, start
/// pushing it at browsers (AC-RT-006, "approved").</item>
/// <item>Relaying never invokes or awaits lifecycle hooks. It only buffers a change, which is why a
/// plugin event can never slow down or fail the work that produced it.</item>
/// </list>
/// </remarks>
public sealed class PluginEventRelay(
    IRealtimeUpdatePublisher publisher,
    RealtimeOptions options,
    ILogger<PluginEventRelay> logger) : ITrustedPluginEventPublisher
{
    public void Publish(PluginEvent pluginEvent)
    {
        ArgumentNullException.ThrowIfNull(pluginEvent);

        if (!options.IsPluginEventRelayed(pluginEvent.EventType))
        {
            logger.LogDebug(
                "Plugin event {EventType} is not approved for realtime relay; dropping.",
                pluginEvent.EventType);
            return;
        }

        var change = new RealtimePluginEventChange(
            pluginEvent.WorkspaceId,
            pluginEvent.EventType,
            pluginEvent.IssueId,
            // A plugin event originates outside any request, so there is no inbound correlation id
            // to propagate; a fresh one still ties the relay's logs to what a client received.
            CorrelationContext.FromHeaderOrNew(null).CorrelationId,
            pluginEvent.OccurredAt ?? DateTimeOffset.UtcNow);

        try
        {
            // Fire-and-forget by contract: the publisher completes as soon as the change is buffered
            // or dropped, so this never blocks the plugin and has nothing to await.
            _ = publisher.PublishAsync(change);
        }
        catch (Exception ex)
        {
            // A plugin publishes after its own work is already done, so a realtime failure must stay
            // invisible to it rather than fail the delivery that produced the event.
            logger.LogWarning(ex, "Failed to relay plugin event {EventType}.", pluginEvent.EventType);
        }
    }
}

/// <summary>
/// The <see cref="IPluginEventPublisher"/> a reflection-loaded/third-party plugin actually resolves
/// once realtime is enabled, replacing the <see cref="NullPluginEventPublisher"/> registered by
/// <c>AddAnvilboardApplication</c>.
/// </summary>
/// <remarks>
/// Delegates to the exact same approval-gated relay a first-class, host-authenticated caller (the
/// webhook endpoint, via <see cref="ITrustedPluginEventPublisher"/>) uses, so enabling realtime
/// restores live delivery for every plugin using the public contract rather than leaving it a silent
/// no-op. It is a distinct type from <see cref="PluginEventRelay"/> — not itself an
/// <see cref="IHostOnlyPluginCapability"/> — specifically so it keeps flowing to plugin constructors
/// while <see cref="ITrustedPluginEventPublisher"/>/<see cref="PluginEventRelay"/> do not (see
/// <c>Anvilboard.Infrastructure.Plugins.PluginRegistry</c>).
/// </remarks>
public sealed class PublicPluginEventPublisher(PluginEventRelay relay) : IPluginEventPublisher
{
    public void Publish(PluginEvent pluginEvent) => relay.Publish(pluginEvent);
}
