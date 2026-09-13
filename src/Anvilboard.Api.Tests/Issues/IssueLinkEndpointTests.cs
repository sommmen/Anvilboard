using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Issues;

/// <summary>
/// Exercises <c>PATCH /api/issues/{id}/links/{linkId}</c>. Before this route a mistyped link could
/// only be deleted and re-created, which reset <c>CreatedAt</c> and orphaned its activity (MAJ-011).
/// </summary>
public sealed class IssueLinkEndpointTests
{
    [Fact]
    public async Task Update_ChangesTypeAndPreservesCreatedAt()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, target) = await CreateIssuePairAsync(client);
        var (linkId, createdAt) = await CreateLinkAsync(client, source, target, "RELATED", "why they relate");

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{linkId}",
            new { type = "BLOCKS" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("BLOCKS", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("why they relate", payload.RootElement.GetProperty("description").GetString());
        Assert.Equal(createdAt, payload.RootElement.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Update_DescriptionOnly_LeavesTypeUnchanged()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, target) = await CreateIssuePairAsync(client);
        var (linkId, _) = await CreateLinkAsync(client, source, target, "RELATED");

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{linkId}",
            new { description = "added later" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("RELATED", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("added later", payload.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Update_ShowsUpInActivityFeedOfBothIssues()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, target) = await CreateIssuePairAsync(client);
        var (linkId, _) = await CreateLinkAsync(client, source, target, "RELATED");

        var update = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{linkId}",
            new { type = "BLOCKS" },
            CancellationToken.None);
        update.EnsureSuccessStatusCode();

        Assert.True(await HasLinkUpdatedEventAsync(client, source), "Source issue feed is missing the update.");
        Assert.True(await HasLinkUpdatedEventAsync(client, target), "Target issue feed is missing the update.");
    }

    [Fact]
    public async Task Update_OntoExistingPairType_ReturnsConflict()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, target) = await CreateIssuePairAsync(client);
        await CreateLinkAsync(client, source, target, "BLOCKS");
        var (secondId, _) = await CreateLinkAsync(client, source, target, "RELATED");

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{secondId}",
            new { type = "BLOCKS" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Update_NoFieldsSupplied_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, target) = await CreateIssuePairAsync(client);
        var (linkId, _) = await CreateLinkAsync(client, source, target, "RELATED");

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{linkId}",
            new { },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_UnknownLink_ReturnsNotFound()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var (source, _) = await CreateIssuePairAsync(client);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{source}/links/{Guid.NewGuid()}",
            new { type = "BLOCKS" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{Guid.NewGuid()}/links/{Guid.NewGuid()}",
            new { type = "BLOCKS" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<bool> HasLinkUpdatedEventAsync(HttpClient client, Guid issueId)
    {
        var response = await client.GetAsync($"/api/issues/{issueId}/activity", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("entries").EnumerateArray()
            .Any(entry => entry.GetProperty("type").GetString() == "IssueLinkUpdated");
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }

    private static async Task<(Guid LinkId, DateTimeOffset CreatedAt)> CreateLinkAsync(
        HttpClient client, Guid source, Guid target, string type, string? description = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/issues/{source}/links",
            new { targetIssueId = target, type, description },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return (payload.RootElement.GetProperty("id").GetGuid(),
            payload.RootElement.GetProperty("createdAt").GetDateTimeOffset());
    }

    private static async Task<(Guid Source, Guid Target)> CreateIssuePairAsync(HttpClient client)
    {
        var team = await client.PostAsJsonAsync(
            "/api/teams",
            new { name = "Engineering", key = "ENG" },
            CancellationToken.None);
        team.EnsureSuccessStatusCode();
        using var teamPayload = JsonDocument.Parse(await team.Content.ReadAsStringAsync(CancellationToken.None));
        var teamId = teamPayload.RootElement.GetProperty("id").GetGuid();

        return (await CreateIssueAsync(client, teamId, "Source issue"),
            await CreateIssueAsync(client, teamId, "Target issue"));
    }

    private static async Task<Guid> CreateIssueAsync(HttpClient client, Guid teamId, string title)
    {
        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId, title },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }
}
