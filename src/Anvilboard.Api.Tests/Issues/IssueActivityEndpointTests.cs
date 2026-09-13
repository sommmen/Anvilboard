using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Issues;

/// <summary>
/// Exercises the issue activity and comment read paths. Both were write-only before this change:
/// activity accumulated in the database with no way to read it (MAJ-008) and the comment thread was
/// only ever visible in the response to the POST that created it.
/// </summary>
public sealed class IssueActivityEndpointTests
{
    [Fact]
    public async Task Activity_AfterMutations_ReturnsRenderedNewestFirstFeed()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);
        await AddCommentAsync(client, issueId, "First note");

        var response = await client.GetAsync($"/api/issues/{issueId}/activity", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var entries = payload.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("text").GetString())));

        // Newest first: the comment we just added must precede the creation event.
        var timestamps = entries.Select(entry => entry.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(timestamps.OrderByDescending(value => value).ToList(), timestamps);
        Assert.Contains(entries, entry => entry.GetProperty("type").GetString() == "CommentAdded");
    }

    [Fact]
    public async Task Activity_LimitOne_PagesThroughCursor()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);
        await AddCommentAsync(client, issueId, "Note one");
        await AddCommentAsync(client, issueId, "Note two");

        var first = await client.GetAsync($"/api/issues/{issueId}/activity?limit=1", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstPage = JsonDocument.Parse(await first.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Single(firstPage.RootElement.GetProperty("entries").EnumerateArray());
        var cursor = firstPage.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        var firstId = firstPage.RootElement.GetProperty("entries")[0].GetProperty("id").GetGuid();

        var second = await client.GetAsync(
            $"/api/issues/{issueId}/activity?limit=1&cursor={Uri.EscapeDataString(cursor!)}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondPage = JsonDocument.Parse(await second.Content.ReadAsStringAsync(CancellationToken.None));
        var secondId = secondPage.RootElement.GetProperty("entries")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task Activity_MalformedCursor_ReturnsProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.GetAsync(
            $"/api/issues/{issueId}/activity?cursor=not-a-cursor",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_LimitAboveMaximum_ReturnsProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.GetAsync($"/api/issues/{issueId}/activity?limit=10000", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_UnknownIssue_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync($"/api/issues/{Guid.NewGuid()}/activity", CancellationToken.None);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"Expected a scope denial, got {response.StatusCode}.");
    }

    [Fact]
    public async Task Activity_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/issues/{Guid.NewGuid()}/activity", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Comments_AfterReload_ReturnsPersistedThreadInOrder()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);
        await AddCommentAsync(client, issueId, "Oldest");
        await AddCommentAsync(client, issueId, "Newest");

        var response = await client.GetAsync($"/api/issues/{issueId}/comments", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var comments = payload.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, comments.Count);
        Assert.Equal("Oldest", comments[0].GetProperty("body").GetString());
        Assert.Equal("Newest", comments[1].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Comments_NoComments_ReturnsEmptyArray()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.GetAsync($"/api/issues/{issueId}/comments", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Comments_UnknownIssue_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync($"/api/issues/{Guid.NewGuid()}/comments", CancellationToken.None);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"Expected a scope denial, got {response.StatusCode}.");
    }

    [Fact]
    public async Task Comments_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/issues/{Guid.NewGuid()}/comments", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }

    private static async Task AddCommentAsync(HttpClient client, Guid issueId, string body)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/issues/{issueId}/comments",
            new { body },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateIssueAsync(HttpClient client)
    {
        var team = await client.PostAsJsonAsync(
            "/api/teams",
            new { name = "Engineering", key = "ENG" },
            CancellationToken.None);
        team.EnsureSuccessStatusCode();
        using var teamPayload = JsonDocument.Parse(await team.Content.ReadAsStringAsync(CancellationToken.None));
        var teamId = teamPayload.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId, title = "Issue with history" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }
}
