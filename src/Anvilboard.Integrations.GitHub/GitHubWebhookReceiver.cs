using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.GitHub;

/// <summary>
/// Handles GitHub's <c>issues</c> webhook event, mounted by the host at
/// <c>/webhooks/github</c> (see <see cref="IWebhookReceiver.RoutePrefix"/>). Verifies the
/// <c>X-Hub-Signature-256</c> HMAC before trusting the payload.
/// </summary>
public sealed class GitHubWebhookReceiver(IOptionsMonitor<GitHubOptions> options) : IWebhookReceiver
{
    public PluginManifest Manifest { get; } = new("github", "GitHub Issues", "1.0.0");

    public string RoutePrefix => "github";

    /// <summary>
    /// Namespaced identifier for a merged pull request. Operators approve relay of this exact value
    /// through <c>Realtime:RelayedPluginEventTypes</c>.
    /// </summary>
    public const string PullRequestMergedEventType = "github.pull_request.merged";

    public Task<WebhookResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        if (!string.IsNullOrEmpty(opts.WebhookSecret) && !IsSignatureValid(request, opts.WebhookSecret))
        {
            return Task.FromResult(WebhookResult.Reject("Invalid signature."));
        }

        var eventType = request.Headers.FirstOrDefault(h => string.Equals(h.Key, "X-GitHub-Event", StringComparison.OrdinalIgnoreCase)).Value;
        if (eventType == "pull_request")
        {
            // A merged pull request changes nothing on the board, but a board watching an issue it
            // closes still wants to know. Reported as an event rather than an issue so it reaches
            // clients without a lifecycle hook or a synthetic mutation.
            return Task.FromResult(IsMergedPullRequest(request.RawBody)
                ? WebhookResult.Accept(null, null, [PullRequestMergedEventType], opts.TeamKey)
                : WebhookResult.Accept());
        }

        if (eventType != "issues")
        {
            // Acknowledge everything else (ping, push, ...) without producing an issue.
            return Task.FromResult(WebhookResult.Accept());
        }

        GitHubIssueEventDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize(request.RawBody, GitHubWebhookJsonContext.Default.GitHubIssueEventDto);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(WebhookResult.Reject($"Malformed payload: {ex.Message}"));
        }

        if (payload?.Issue is null || payload.Repository is null)
        {
            return Task.FromResult(WebhookResult.Reject("Missing issue or repository in payload."));
        }

        var normalized = payload.Issue.ToNormalizedIssue(payload.Repository.FullName, opts.TeamKey);
        return Task.FromResult(WebhookResult.Accept([normalized], null, null, opts.TeamKey));
    }

    /// <summary>
    /// A pull request is only "merged" when it closed <em>and</em> actually merged — GitHub sends
    /// the same <c>closed</c> action for a pull request that was abandoned.
    /// </summary>
    private static bool IsMergedPullRequest(string rawBody)
    {
        GitHubPullRequestEventDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize(rawBody, GitHubWebhookJsonContext.Default.GitHubPullRequestEventDto);
        }
        catch (JsonException)
        {
            return false;
        }

        return payload?.Action == "closed" && payload.PullRequest?.Merged == true;
    }

    private static bool IsSignatureValid(WebhookRequest request, string secret)
    {
        var headerEntry = request.Headers.FirstOrDefault(h => string.Equals(h.Key, "X-Hub-Signature-256", StringComparison.OrdinalIgnoreCase));
        if (headerEntry.Value is not { } header || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Convert.FromHexString(header["sha256=".Length..]);
        var actual = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.RawBody));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

internal sealed class GitHubIssueEventDto
{
    public string? Action { get; set; }
    public GitHubIssueDto? Issue { get; set; }
    public GitHubRepositoryDto? Repository { get; set; }
}

internal sealed class GitHubRepositoryDto
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";
}

internal sealed class GitHubPullRequestEventDto
{
    public string? Action { get; set; }

    [JsonPropertyName("pull_request")]
    public GitHubPullRequestDto? PullRequest { get; set; }
    public GitHubRepositoryDto? Repository { get; set; }
}

internal sealed class GitHubPullRequestDto
{
    public bool Merged { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubIssueEventDto))]
[JsonSerializable(typeof(GitHubPullRequestEventDto))]
internal sealed partial class GitHubWebhookJsonContext : JsonSerializerContext;
