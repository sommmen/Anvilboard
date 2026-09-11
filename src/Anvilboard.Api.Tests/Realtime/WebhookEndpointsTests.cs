using System.Net;
using System.Net.Http.Json;
using System.Text;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Realtime;

/// <summary>
/// Covers the <c>POST /webhooks/{provider}</c> team-key resolution branch in
/// <c>WebhookEndpoints</c>: a webhook's <c>TeamKey</c> must resolve to exactly one local team
/// before the delivery is trusted with a workspace.
/// </summary>
public sealed class WebhookEndpointsTests
{
    private const string IssuePayload = """
        {"action":"opened","issue":{"number":7,"title":"From webhook","body":null,"state":"open","html_url":"https://github.test/org/repo/issues/7","labels":[],"updated_at":"2026-01-02T03:04:05+00:00"},"repository":{"full_name":"org/repo"}}
        """;

    [Fact]
    public async Task PostWebhook_TeamKeyExistsInTwoWorkspaces_Returns400InsteadOfGuessingATenant()
    {
        // Team keys are unique only within a workspace (see TeamConfiguration), so two different
        // workspaces can legally configure the same key ("ENG", the GitHub integration's default
        // team key). A webhook payload carries no workspace identity of its own, so the endpoint
        // must refuse to guess which workspace's team to file the issue under.
        await using var factory = new ApiFactory();

        using var clientA = factory.CreateClient();
        var cookieA = await ApiFactory.BootstrapAndGetSessionCookieAsync(clientA, "workspace-a");
        clientA.DefaultRequestHeaders.Add("Cookie", cookieA);
        var teamAResponse = await clientA.PostAsJsonAsync("/api/teams", new { name = "Engineering A", key = "ENG" });
        teamAResponse.EnsureSuccessStatusCode();

        await factory.SeedAdditionalWorkspaceAsync("workspace-b", "admin-b");
        using var clientB = factory.CreateClient();
        var cookieB = await ApiFactory.LoginAndGetSessionCookieAsync(clientB, "admin-b");
        clientB.DefaultRequestHeaders.Add("Cookie", cookieB);
        var teamBResponse = await clientB.PostAsJsonAsync("/api/teams", new { name = "Engineering B", key = "ENG" });
        teamBResponse.EnsureSuccessStatusCode();

        using var anonymousClient = factory.CreateClient();
        anonymousClient.DefaultRequestHeaders.Add("X-GitHub-Event", "issues");
        var webhookResponse = await anonymousClient.PostAsync(
            "/webhooks/github",
            new StringContent(IssuePayload, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, webhookResponse.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_TeamKeyMatchesNoLocalTeam_Returns400()
    {
        // No workspace has configured a team keyed "ENG" (the GitHub integration's default), so the
        // delivery cannot be attributed to any tenant and must be rejected rather than silently
        // dropped or misfiled.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(client);

        client.DefaultRequestHeaders.Add("X-GitHub-Event", "issues");
        var webhookResponse = await client.PostAsync(
            "/webhooks/github",
            new StringContent(IssuePayload, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, webhookResponse.StatusCode);
    }
}
