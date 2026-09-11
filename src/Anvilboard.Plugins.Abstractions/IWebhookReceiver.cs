namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// A push-based plugin that turns an inbound webhook (GitHub, Linear, Slack, a custom agent
/// pushing work, ...) directly into normalized issues/comments, instead of waiting for the next
/// poll. The host maps <see cref="RoutePrefix"/> to <c>/webhooks/{RoutePrefix}</c> and forwards
/// the raw request there; this keeps HTTP/signature-verification concerns entirely inside the
/// plugin rather than leaking provider-specific parsing into the API host project.
/// </summary>
public interface IWebhookReceiver : IAnvilboardPlugin
{
    /// <summary>
    /// URL segment this receiver is mounted under, e.g. "github" for
    /// <c>POST /webhooks/github</c>. Must be unique across all registered plugins.
    /// </summary>
    string RoutePrefix { get; }

    /// <summary>
    /// Validates and parses one inbound webhook delivery. Implementations are responsible for
    /// verifying any provider signature header found in <paramref name="request"/> and should
    /// return <see cref="WebhookResult.Rejected"/> (not throw) for invalid signatures so the host
    /// can respond with the correct HTTP status without treating it as a server error.
    /// </summary>
    Task<WebhookResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken);
}

/// <summary>Transport-agnostic view of the inbound HTTP request a webhook receiver needs.</summary>
public sealed record WebhookRequest(
    IReadOnlyDictionary<string, string> Headers,
    string RawBody);

/// <summary>Outcome of processing one webhook delivery.</summary>
public sealed record WebhookResult
{
    public required bool Accepted { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<NormalizedIssue> Issues { get; init; } = [];
    public IReadOnlyList<NormalizedComment> Comments { get; init; } = [];

    /// <summary>
    /// Namespaced event types (e.g. <c>github.pull_request.merged</c>) this delivery represents but
    /// which produce no issue or comment. The host relays approved ones to connected clients through
    /// <see cref="IPluginEventPublisher"/>; a receiver reports what happened and never decides
    /// whether anyone is told, which is what keeps event approval an operator decision.
    /// </summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    /// <summary>The local team key authenticated webhook work belongs to.</summary>
    public string? TeamKey { get; init; }

    // Keep this exact overload for plugins compiled before event relaying was introduced.
    public static WebhookResult Accept(
        IReadOnlyList<NormalizedIssue>? issues = null,
        IReadOnlyList<NormalizedComment>? comments = null) =>
        new() { Accepted = true, Issues = issues ?? [], Comments = comments ?? [] };

    // Keep this exact three-parameter CLR signature too: it shipped before team-key routing was
    // introduced, and a plugin assembly compiled against it binds to this overload by signature at
    // load time, not by recompiling against the newer four-parameter one. Removing it would make a
    // previously-working plugin DLL fail with a MissingMethodException at call time even though
    // nothing in its own source changed.
    public static WebhookResult Accept(
        IReadOnlyList<NormalizedIssue>? issues,
        IReadOnlyList<NormalizedComment>? comments,
        IReadOnlyList<string>? eventTypes) =>
        Accept(issues, comments, eventTypes, teamKey: null);

    // Restores the pre-team-key source-compatible call surface for named-argument-only calls
    // like `Accept(eventTypes: events)`, which compiled before team-key routing was introduced
    // but stopped compiling once `issues`/`comments` above became required. `eventTypes` is the
    // only parameter here (so this overload can't tie with the two- or three-parameter ones on
    // a zero- or one-argument positional call).
    public static WebhookResult Accept(IReadOnlyList<string>? eventTypes) =>
        Accept(issues: null, comments: null, eventTypes, teamKey: null);

    /// <summary>Accepts a delivery that also carries approved event and trusted team routing data.</summary>
    public static WebhookResult Accept(
        IReadOnlyList<NormalizedIssue>? issues,
        IReadOnlyList<NormalizedComment>? comments,
        IReadOnlyList<string>? eventTypes,
        string? teamKey) =>
        new() { Accepted = true, Issues = issues ?? [], Comments = comments ?? [], EventTypes = eventTypes ?? [], TeamKey = teamKey };

    public static WebhookResult Reject(string reason) => new() { Accepted = false, RejectionReason = reason };
}
