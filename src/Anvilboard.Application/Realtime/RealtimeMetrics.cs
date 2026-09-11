using System.Diagnostics.Metrics;

namespace Anvilboard.Application.Realtime;

/// <summary>
/// Operational instruments for the realtime pipeline. These exist to make the documented
/// coalesce/drop policy observable rather than silent: an operator must be able to tell a dropped
/// presentation notification apart from a failed core write
/// (<c>docs/features/realtime-updates.md</c>, "Slow and disconnected clients").
/// </summary>
public sealed class RealtimeMetrics : IDisposable
{
    public const string MeterName = "Anvilboard.Realtime";

    private readonly Meter meter;
    private readonly Histogram<double> publicationLatency;
    private readonly Counter<long> published;
    private readonly Counter<long> coalesced;
    private readonly Counter<long> dropped;
    private readonly Counter<long> sendFailures;
    private int activeConnections;

    public RealtimeMetrics(IMeterFactory meterFactory)
    {
        meter = meterFactory.Create(MeterName);

        publicationLatency = meter.CreateHistogram<double>(
            "anvilboard.realtime.publication.latency",
            unit: "ms",
            description: "Time from the originating commit to the attempted transport send.");
        published = meter.CreateCounter<long>(
            "anvilboard.realtime.published",
            description: "Changes handed off to the realtime pipeline after a committed mutation.");
        coalesced = meter.CreateCounter<long>(
            "anvilboard.realtime.coalesced",
            description: "Changes superseded by a newer change for the same coalescing key.");
        dropped = meter.CreateCounter<long>(
            "anvilboard.realtime.dropped",
            description: "Changes discarded because the bounded pending buffer was saturated.");
        sendFailures = meter.CreateCounter<long>(
            "anvilboard.realtime.send.failures",
            description: "Transport send attempts that threw; the committed mutation is unaffected.");

        meter.CreateObservableGauge(
            "anvilboard.realtime.connections.active",
            () => Volatile.Read(ref activeConnections),
            description: "Authorized realtime connections currently subscribed to a workspace group.");
    }

    public void RecordPublished(string eventType) =>
        published.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordCoalesced(string eventType) =>
        coalesced.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordDropped(string eventType) =>
        dropped.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordSendFailure(string eventType) =>
        sendFailures.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordPublicationLatency(RealtimeChange change) =>
        publicationLatency.Record(
            Math.Max(0, (DateTimeOffset.UtcNow - change.OccurredAt).TotalMilliseconds),
            new KeyValuePair<string, object?>("event.type", change.EventType));

    /// <summary>Called only once a connection is authorized and joined to its workspace group.</summary>
    public void ConnectionOpened() => Interlocked.Increment(ref activeConnections);

    public void ConnectionClosed() => Interlocked.Decrement(ref activeConnections);

    public void Dispose() => meter.Dispose();
}
