using System.Security.Cryptography;
using System.Text;
using Anvilboard.Domain;
using Anvilboard.Integrations.GitHub;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.GitHub.Tests;

public sealed class GitHubWebhookReceiverTests
{
    private const string Secret = "test-secret";
    private const string IssuePayload = """
        {"action":"opened","issue":{"number":42,"title":"Fix webhook","body":"Details","state":"open","html_url":"https://github.test/org/repo/issues/42","labels":[{"name":"bug"}],"updated_at":"2026-01-02T03:04:05+00:00"},"repository":{"full_name":"org/repo"}}
        """;

    [Fact]
    public void WebhookResult_AcceptOverloads_PreserveLegacyAndEventAwareCalls()
    {
        var legacy = WebhookResult.Accept([], []);
        var eventAware = WebhookResult.Accept([], [], [GitHubWebhookReceiver.PullRequestMergedEventType], "ENG");

        Assert.True(legacy.Accepted);
        Assert.Empty(legacy.EventTypes);
        Assert.Equal("ENG", eventAware.TeamKey);
    }

    [Fact]
    public async Task HandleAsync_ValidIssuesEvent_MapsNormalizedIssue()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("issues", IssuePayload, signed: true), CancellationToken.None);

        var issue = Assert.Single(result.Issues);
        Assert.True(result.Accepted);
        Assert.Equal(IntegrationProvider.GitHub, issue.Provider);
        Assert.Equal("org/repo#42", issue.SourceKey);
        Assert.Equal("ENG", issue.TeamKey);
        Assert.Equal("ENG", result.TeamKey);
        Assert.Equal("Fix webhook", issue.Title);
        Assert.Equal("Details", issue.Description);
        Assert.Equal(IssueStatus.Backlog, issue.SuggestedStatus);
        Assert.Equal(["bug"], issue.LabelNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task HandleAsync_InvalidOrMissingSignature_Rejects(bool? signed)
    {
        var request = CreateRequest("issues", IssuePayload, signed == true);
        if (signed is null)
        {
            request = new WebhookRequest(new Dictionary<string, string> { ["X-GitHub-Event"] = "issues" }, IssuePayload);
        }

        var result = await CreateReceiver().HandleAsync(request, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("Invalid signature.", result.RejectionReason);
    }

    [Fact]
    public async Task HandleAsync_MalformedIssuesPayload_Rejects()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("issues", "{", signed: true), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.StartsWith("Malformed payload:", result.RejectionReason);
    }

    [Fact]
    public async Task HandleAsync_NonIssueEvent_AcknowledgesWithoutIssue()
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("ping", "{}", signed: true), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task HandleAsync_MergedPullRequest_ReportsMergedEventType()
    {
        const string body = """{"action":"closed","pull_request":{"merged":true},"repository":{"full_name":"org/repo"}}""";

        var result = await CreateReceiver().HandleAsync(CreateRequest("pull_request", body, signed: true), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Empty(result.Issues);
        Assert.Equal([GitHubWebhookReceiver.PullRequestMergedEventType], result.EventTypes);
        Assert.Equal("ENG", result.TeamKey);
    }

    [Theory]
    [InlineData("""{"action":"closed","pull_request":{"merged":false}}""")]
    [InlineData("""{"action":"opened","pull_request":{"merged":false}}""")]
    [InlineData("{")]
    public async Task HandleAsync_PullRequestThatDidNotMerge_ReportsNoEventType(string body)
    {
        var result = await CreateReceiver().HandleAsync(CreateRequest("pull_request", body, signed: true), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Empty(result.EventTypes);
    }

    private static GitHubWebhookReceiver CreateReceiver() => new(new StaticOptionsMonitor<GitHubOptions>(new GitHubOptions { TeamKey = "ENG", WebhookSecret = Secret }));

    private static WebhookRequest CreateRequest(string eventType, string body, bool signed) => new(
        new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = eventType,
            ["X-Hub-Signature-256"] = signed ? $"sha256={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant()}" : "sha256=00",
        },
        body);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
