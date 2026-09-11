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

    public static WebhookResult Accept(
        IReadOnlyList<NormalizedIssue>? issues = null,
        IReadOnlyList<NormalizedComment>? comments = null,
        IReadOnlyList<string>? eventTypes = null) =>
        new() { Accepted = true, Issues = issues ?? [], Comments = comments ?? [], EventTypes = eventTypes ?? [] };

    public static WebhookResult Reject(string reason) => new() { Accepted = false, RejectionReason = reason };
}
