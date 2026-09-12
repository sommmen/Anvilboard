using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Artifacts;

/// <summary>
/// Exercises the artifact routes against the real host, so route mapping, DI resolution of
/// <c>IArtifactService</c>/<c>IArtifactStore</c>, permission enforcement, and JSON shape are all
/// verified together rather than assumed from the unit tests.
/// </summary>
public sealed class ArtifactEndpointTests
{
    [Fact]
    public async Task Attach_ThenList_ReturnsPersistedArtifact()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var attach = await client.PostAsJsonAsync($"/api/issues/{issueId}/artifacts", new
        {
            kind = "deployment",
            title = "Staging deploy",
            contentReference = "https://deploy.example/123",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        using var created = JsonDocument.Parse(await attach.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("deployment", created.RootElement.GetProperty("kind").GetString());
        Assert.Equal("local", created.RootElement.GetProperty("source").GetString());

        var list = await client.GetAsync($"/api/issues/{issueId}/artifacts", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync(CancellationToken.None));
        var artifact = Assert.Single(listed.RootElement.EnumerateArray());
        Assert.Equal("Staging deploy", artifact.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Attach_InlineContent_StoresThroughConfiguredStore()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var attach = await client.PostAsJsonAsync($"/api/issues/{issueId}/artifacts", new
        {
            kind = "file",
            title = "crash.log",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("crash log contents")),
            contentType = "text/plain",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        using var created = JsonDocument.Parse(await attach.Content.ReadAsStringAsync(CancellationToken.None));

        // The registered SqliteArtifactStore produced an opaque reference, so the raw content never
        // becomes the content reference itself.
        var reference = created.RootElement.GetProperty("contentReference").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reference));
        Assert.DoesNotContain("crash log contents", reference);
    }

    [Fact]
    public async Task Attach_UnknownKind_ReturnsValidationProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var response = await client.PostAsJsonAsync($"/api/issues/{issueId}/artifacts", new
        {
            kind = "not-a-kind",
            title = "Title",
            contentReference = "https://example.test",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Attach_UnknownIssue_ReturnsNotFoundProblem()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/issues/{Guid.NewGuid()}/artifacts", new
        {
            kind = "link",
            title = "Title",
            contentReference = "https://example.test",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Delete_RemovesArtifact()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        var attach = await client.PostAsJsonAsync($"/api/issues/{issueId}/artifacts", new
        {
            kind = "link",
            title = "Design doc",
            contentReference = "https://docs.example/design",
        }, CancellationToken.None);
        attach.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await attach.Content.ReadAsStringAsync(CancellationToken.None));
        var artifactId = created.RootElement.GetProperty("id").GetGuid();

        var delete = await client.DeleteAsync($"/api/issues/{issueId}/artifacts/{artifactId}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var list = await client.GetAsync($"/api/issues/{issueId}/artifacts", CancellationToken.None);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Empty(listed.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Attach_WithoutSession_IsRejected()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var issueId = await CreateIssueAsync(client);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync($"/api/issues/{issueId}/artifacts", new
        {
            kind = "link",
            title = "Title",
            contentReference = "https://example.test",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        await factory.SeedWorkflowStatesAsync();
        return client;
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
            new { teamId, title = "Issue with artifacts" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }
}
