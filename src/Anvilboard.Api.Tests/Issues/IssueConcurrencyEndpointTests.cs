using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Issues;

/// <summary>
/// Exercises the HTTP face of the conditional-write contract: <c>expectedVersion</c> on the status
/// and assignee routes, and the 409 problem document a stale caller gets back. The response carries
/// the authoritative version as an extension so a client can refetch and retry without a round trip
/// spent discovering what it should have sent.
/// </summary>
public sealed class IssueConcurrencyEndpointTests
{
    [Fact]
    public async Task Assign_StaleExpectedVersion_Returns409WithCurrentVersion()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId}/assignee",
            new { assigneeId = (Guid?)null, expectedVersion = 99 },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("CONCURRENCY_CONFLICT", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task Assign_MatchingExpectedVersion_SucceedsAndReturnsTheNextVersion()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId}/assignee",
            new { assigneeId = (Guid?)null, expectedVersion = 1 },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(2, payload.RootElement.GetProperty("version").GetInt32());
    }

    /// <summary>
    /// Omitting the field keeps the pre-existing last-writer-wins behaviour, so every client that
    /// has not been taught about versions keeps working unchanged.
    /// </summary>
    [Fact]
    public async Task Assign_WithoutExpectedVersion_StillSucceeds()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId}/assignee",
            new { assigneeId = (Guid?)null },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangeStatus_StaleExpectedVersion_Returns409BeforeAnyWorkflowValidation()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);
        var states = await ListWorkflowStateIdsAsync(client);

        // Deliberately a state the workflow may well refuse: the conflict must win, because a stale
        // caller cannot act on a transition error about a state it has not seen.
        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId}/status",
            new { workflowStateId = states[^1], expectedVersion = 42 },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("CONCURRENCY_CONFLICT", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task CreatedIssue_StartsAtVersionOne()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var teamId = await CreateTeamAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId, title = "Fresh issue" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(1, payload.RootElement.GetProperty("version").GetInt32());
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }

    private static async Task<Guid[]> ListWorkflowStateIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/workflow/states", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return [.. payload.RootElement.EnumerateArray().Select(s => s.GetProperty("id").GetGuid())];
    }

    private static async Task<Guid> CreateTeamAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/teams",
            new { name = "Engineering", key = "ENG" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateIssueAsync(HttpClient client)
    {
        var teamId = await CreateTeamAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId, title = "Concurrency subject" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }
}
