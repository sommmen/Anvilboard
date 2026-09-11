using System.Security.Cryptography;
using System.Text;
using Anvilboard.Domain;
using Anvilboard.Integrations.Linear;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.Linear.Tests;

public sealed class LinearWebhookReceiverTests
{
    private const string Secret = "test-secret";
    private const string IssuePayload = """
        {"type":"Issue","data":{"identifier":"ENG-42","title":"Fix webhook","description":"Details","url":"https://linear.test/ENG-42","updatedAt":"2026-01-02T03:04:05+00:00","state":{"type":"started"},"priority":2,"assignee":{"email":"dev@example.test"},"labels":{"nodes":[{"name":"bug"}]}}}
        """;

    [Fact]
    public async Task HandleAsync_ValidIssueEvent_MapsNormalizedIssue()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest(IssuePayload, signed: true), CancellationToken.None);

        var issue = Assert.Single(result.Issues);
        Assert.True(result.Accepted);
        Assert.Equal(IntegrationProvider.Linear, issue.Provider);
        Assert.Equal("ENG-42", issue.SourceKey);
        Assert.Equal("ENG", issue.TeamKey);
        // Regression: the receiver used to call `WebhookResult.Accept(issues: [normalized])`,
        // which left `WebhookResult.TeamKey` null even though the normalized issue itself carried
        // a team key — so the webhook endpoint could never resolve a trusted workspace for a
        // Linear delivery. It must also be surfaced on the result for team-key routing to work.
        Assert.Equal("ENG", result.TeamKey);
        Assert.Equal(IssueStatus.InProgress, issue.SuggestedStatus);
        Assert.Equal(IssuePriority.High, issue.SuggestedPriority);
        Assert.Equal("dev@example.test", issue.AssigneeEmail);
        Assert.Equal(["bug"], issue.LabelNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task HandleAsync_InvalidOrMissingSignature_Rejects(bool? signed)
    {
        var request = signed is null
            ? new WebhookRequest(new Dictionary<string, string>(), IssuePayload)
            : CreateRequest(IssuePayload, signed.Value);

        var result = await CreateReceiver().HandleAsync(request, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("Invalid signature.", result.RejectionReason);
    }

    [Fact]
    public async Task HandleAsync_MalformedPayload_Rejects()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("{", signed: true), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.StartsWith("Malformed payload:", result.RejectionReason);
    }

    [Fact]
    public async Task HandleAsync_NonIssueEvent_AcknowledgesWithoutIssue()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("{\"type\":\"Comment\"}", signed: true), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Empty(result.Issues);
    }

    private static LinearWebhookReceiver CreateReceiver() => new(new StaticOptionsMonitor<LinearOptions>(new LinearOptions { TeamKey = "ENG", WebhookSecret = Secret }));

    private static WebhookRequest CreateRequest(string body, bool signed) => new(
        new Dictionary<string, string>
        {
            ["Linear-Signature"] = signed ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant() : "00",
        },
        body);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
