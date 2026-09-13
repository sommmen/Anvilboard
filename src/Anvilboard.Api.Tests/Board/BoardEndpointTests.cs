using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Board;

/// <summary>
/// Exercises <c>GET /api/board</c> against the real host. <c>IBoardQueryService</c> was fully
/// implemented but unreachable before this route existed (MAJ-007), so these tests are the first
/// verification that its grouping, ordering, and filter vocabulary survive the HTTP boundary.
/// </summary>
public sealed class BoardEndpointTests
{
    [Fact]
    public async Task Query_WithoutFilters_ReturnsGroupedIssuesAndEchoesDefaults()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await CreateIssueAsync(client, "First issue");

        var response = await client.GetAsync("/api/board", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(25, root.GetProperty("limit").GetInt32());

        var applied = root.GetProperty("appliedQuery");
        Assert.Equal("WorkflowState", applied.GetProperty("groupBy").GetString());
        Assert.Equal("CreatedAt", applied.GetProperty("orderBy").GetString());
        Assert.False(applied.GetProperty("includeArchived").GetBoolean());

        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        var issue = Assert.Single(group.GetProperty("issues").EnumerateArray());
        Assert.Equal("First issue", issue.GetProperty("title").GetString());
        Assert.Equal("Local", issue.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Query_SnakeCaseVocabulary_BindsToEnumMember()
    {
        // The shareable-URL contract is symbolic tokens, and workflow_state must reach
        // BoardGroupBy.WorkflowState rather than 400 on the underscore.
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await CreateIssueAsync(client, "Grouped issue");

        var response = await client.GetAsync("/api/board?groupBy=workflow_state&orderBy=updated_at", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var applied = payload.RootElement.GetProperty("appliedQuery");
        Assert.Equal("WorkflowState", applied.GetProperty("groupBy").GetString());
        Assert.Equal("UpdatedAt", applied.GetProperty("orderBy").GetString());
    }

    [Fact]
    public async Task Query_GroupByAssignee_ReturnsAssigneeGroups()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await CreateIssueAsync(client, "Unassigned issue");

        var response = await client.GetAsync("/api/board?groupBy=ASSIGNEE", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("Assignee", payload.RootElement.GetProperty("appliedQuery").GetProperty("groupBy").GetString());
        Assert.NotEmpty(payload.RootElement.GetProperty("groups").EnumerateArray());
    }

    [Theory]
    [InlineData("groupBy=not_a_grouping")]
    [InlineData("orderBy=not_an_order")]
    [InlineData("syncCondition=not_a_condition")]
    [InlineData("provider=not_a_provider")]
    public async Task Query_UnknownClosedVocabularyToken_ReturnsBadRequest(string queryString)
    {
        // A typo in a closed vocabulary must fail loudly: silently falling back to the default
        // would answer a different question than the URL claims.
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync($"/api/board?{queryString}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_UnknownPriorityValue_MatchesNothingRatherThanFailing()
    {
        // priority is an ordinary filter, not a closed vocabulary: an unknown value correctly
        // matches no issues instead of rejecting the request.
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await CreateIssueAsync(client, "Some issue");

        var response = await client.GetAsync("/api/board?priority=NONEXISTENT", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(0, payload.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Query_LimitAboveMaximum_ReturnsProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/board?limit=5000", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_PageBelowOne_ReturnsProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/board?page=0", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_ForeignWorkflowStateId_IsDenied()
    {
        // Scope resolution happens before the query runs, so a workspace-foreign filter id must
        // never silently widen the board.
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync(
            $"/api/board?workflowStateId={Guid.NewGuid()}",
            CancellationToken.None);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"Expected a scope denial, got {response.StatusCode}.");
    }

    [Fact]
    public async Task Query_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/board", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }

    private static async Task<Guid> CreateIssueAsync(HttpClient client, string title)
    {
        var teamId = await EnsureTeamAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId, title },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> EnsureTeamAsync(HttpClient client)
    {
        var existing = await client.GetAsync("/api/teams", CancellationToken.None);
        existing.EnsureSuccessStatusCode();
        using (var listed = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(CancellationToken.None)))
        {
            foreach (var team in listed.RootElement.EnumerateArray())
            {
                return team.GetProperty("id").GetGuid();
            }
        }

        var created = await client.PostAsJsonAsync(
            "/api/teams",
            new { name = "Engineering", key = "ENG" },
            CancellationToken.None);
        created.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }
}
