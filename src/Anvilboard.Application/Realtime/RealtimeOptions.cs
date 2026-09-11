namespace Anvilboard.Application.Realtime;

/// <summary>
/// Bound from the <c>Realtime</c> configuration section. Both values exist to keep realtime strictly
/// bounded: a burst of mutations can never grow memory without limit, and the dispatcher never sends
/// more often than the debounce window allows.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>
    /// How long the dispatcher waits after the first buffered change before draining, so a burst of
    /// updates to the same issue collapses into a single send (AC-RT-004).
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum number of <em>distinct</em> pending coalescing keys. A change whose key is already
    /// pending always fits, because it replaces the pending value; only a genuinely new key can be
    /// dropped when the buffer is saturated.
    /// </summary>
    public int QueueCapacity { get; set; } = 1024;

    /// <summary>Maximum time a single transport send may block the dispatcher.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time spent sending buffered updates while the host shuts down.</summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Plugin event types approved for relay to connected clients, e.g.
    /// <c>github.pull_request.merged</c>. Empty by default: a plugin gaining a new event type never
    /// starts reaching browsers until an operator explicitly approves it here (AC-RT-006).
    /// </summary>
    public List<string> RelayedPluginEventTypes { get; set; } = [];

    /// <summary>Whether <paramref name="eventType"/> is approved for relay.</summary>
    public bool IsPluginEventRelayed(string eventType) =>
        RelayedPluginEventTypes.Contains(eventType, StringComparer.OrdinalIgnoreCase);
}
