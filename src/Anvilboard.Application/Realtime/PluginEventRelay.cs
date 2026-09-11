using Anvilboard.Application.Automation;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Anvilboard.Application.Realtime;

/// <summary>
/// Maps plugin events onto the same coalescing realtime pipeline committed mutations use, so a
/// plugin gets live delivery without a second transport, a second security model, or any ability to
/// reach a client another way.
/// </summary>
/// <remarks>
/// Two properties make this safe to expose to third-party plugins:
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
    ILogger<PluginEventRelay> logger) : IPluginEventPublisher
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
