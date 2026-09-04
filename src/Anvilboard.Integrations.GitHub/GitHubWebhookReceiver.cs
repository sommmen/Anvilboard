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

    public Task<WebhookResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        if (!string.IsNullOrEmpty(opts.WebhookSecret) && !IsSignatureValid(request, opts.WebhookSecret))
        {
            return Task.FromResult(WebhookResult.Reject("Invalid signature."));
        }

        var eventType = request.Headers.FirstOrDefault(h => string.Equals(h.Key, "X-GitHub-Event", StringComparison.OrdinalIgnoreCase)).Value;
        if (eventType != "issues")
        {
            // Acknowledge everything else (ping, pull_request, ...) without producing an issue.
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
        return Task.FromResult(WebhookResult.Accept(issues: [normalized]));
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubIssueEventDto))]
internal sealed partial class GitHubWebhookJsonContext : JsonSerializerContext;
