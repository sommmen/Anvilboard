using System.Net;
using System.Net.Http.Json;
using System.Text;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Domain;

namespace Anvilboard.Api.Tests.Realtime;

/// <summary>
/// Covers the webhook pause gate (<c>docs/plans/integration-sync-health.md</c> §8.5) — the fix for
/// a paused integration still ingesting pushed deliveries, which made "paused" mean nothing.
/// </summary>
public sealed class WebhookPauseGateTests
{
    private const string IssuePayload = """
        {"action":"opened","issue":{"number":7,"title":"From webhook","body":null,"state":"open","html_url":"https://github.test/org/repo/issues/7","labels":[],"updated_at":"2026-01-02T03:04:05+00:00"},"repository":{"full_name":"org/repo"}}
        """;

    [Fact]
    public async Task PostWebhook_IntegrationPaused_Returns409AndIngestsNothing()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamResponse>();
        Assert.NotNull(team);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Paused);

        var response = await PostWebhookAsync(factory);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("INTEGRATION_PAUSED", problem?.Error);

        // The point of the gate is not the status code but the absence of a write: a 409 that still
        // ingested would leave "paused" just as meaningless as no gate at all.
        Assert.Equal(0, await factory.CountIssuesInTeamAsync(team.Id));
    }

    [Fact]
    public async Task PostWebhook_IntegrationEnabled_IsIngestedNormally()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamResponse>();
        Assert.NotNull(team);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Enabled);

        var response = await PostWebhookAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await factory.CountIssuesInTeamAsync(team.Id));
    }

    [Fact]
    public async Task PostWebhook_NoIntegrationRowAtAll_IsStillIngested()
    {
        // Configuration-only installs (an ingestion plugin driven purely by host settings, with no
        // Integration row) predate this feature. Refusing them would break working deployments to
        // enforce a paused state they never entered.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamResponse>();
        Assert.NotNull(team);

        var response = await PostWebhookAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await factory.CountIssuesInTeamAsync(team.Id));
    }

    [Fact]
    public async Task PostWebhook_AnotherProviderPaused_DoesNotBlockThisOne()
    {
        // Pausing Linear must not stop GitHub deliveries; the gate is keyed on the provider the
        // delivery actually arrived for.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamResponse>();
        Assert.NotNull(team);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.Linear, IntegrationStatus.Paused);

        var response = await PostWebhookAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await factory.CountIssuesInTeamAsync(team.Id));
    }

    [Fact]
    public async Task PostWebhook_RemovedIntegration_IsNotTreatedAsPaused()
    {
        // A removed integration is not a paused one; excluding Removed rows keeps a deleted
        // configuration from permanently 409-ing a provider that a config-only install still serves.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamResponse>();
        Assert.NotNull(team);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Removed);

        var response = await PostWebhookAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await factory.CountIssuesInTeamAsync(team.Id));
    }

    private static async Task<HttpResponseMessage> PostWebhookAsync(ApiFactory factory)
    {
        using var anonymousClient = factory.CreateClient();
        anonymousClient.DefaultRequestHeaders.Add("X-GitHub-Event", "issues");
        return await anonymousClient.PostAsync(
            "/webhooks/github",
            new StringContent(IssuePayload, Encoding.UTF8, "application/json"),
            CancellationToken.None);
    }

    private sealed record TeamResponse(Guid Id);

    private sealed record ErrorResponse(string Error);
}
