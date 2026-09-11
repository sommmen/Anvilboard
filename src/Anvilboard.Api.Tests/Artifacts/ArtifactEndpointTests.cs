using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Api.Tests.Artifacts;

/// <summary>
/// End-to-end coverage of `Anvilboard.Api/Endpoints/ArtifactEndpoints.cs`
/// (`docs/plans/artifacts.md` §9, §15) through the real host, including the error-code to
/// HTTP-status mapping and the cross-surface list ordering guarantee (AC-ART-103).
/// </summary>
public sealed class ArtifactEndpointTests
{
    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/issues/{Guid.NewGuid()}/artifacts", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attach_ThenList_ThenDelete_HappyPath()
    {
        await using var context = await ApiTestContext.CreateAsync();

        var attach = await context.Client.PostAsJsonAsync(
            $"/api/issues/{context.IssueId}/artifacts",
            new { kind = "link", title = "Failing CI run", contentReference = "https://ci.example.test/run/42", actorId = context.MemberId },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var created = await attach.Content.ReadFromJsonAsync<ArtifactDto>(CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal("link", created.Kind);
        Assert.Equal("local", created.Source);
        Assert.Equal(context.MemberId, created.AddedById);
        Assert.Equal(
            $"/api/issues/{context.IssueId}/artifacts/{created.Id}",
            attach.Headers.Location?.ToString());

        var listed = await context.Client.GetFromJsonAsync<List<ArtifactDto>>(
            $"/api/issues/{context.IssueId}/artifacts", CancellationToken.None);
        Assert.Equal(created.Id, Assert.Single(listed!).Id);

        var delete = await context.Client.DeleteAsync(
            $"/api/issues/{context.IssueId}/artifacts/{created.Id}?actorId={context.MemberId}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await context.Client.GetFromJsonAsync<List<ArtifactDto>>(
            $"/api/issues/{context.IssueId}/artifacts", CancellationToken.None);
        Assert.Empty(afterDelete!);
    }

    [Fact]
    public async Task List_IssueWithNoArtifacts_ReturnsEmptyArrayNotNotFound()
    {
        await using var context = await ApiTestContext.CreateAsync();

        var response = await context.Client.GetAsync(
            $"/api/issues/{context.IssueId}/artifacts", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<ArtifactDto>>(CancellationToken.None))!);
    }

    [Fact]
    public async Task List_OrderingMatchesTheServiceSurface()
    {
        await using var context = await ApiTestContext.CreateAsync();

        for (var i = 0; i < 3; i++)
        {
            var response = await context.Client.PostAsJsonAsync(
                $"/api/issues/{context.IssueId}/artifacts",
                new { kind = "link", title = $"Artifact {i}", contentReference = $"https://example.test/{i}", actorId = context.MemberId },
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var overRest = await context.Client.GetFromJsonAsync<List<ArtifactDto>>(
            $"/api/issues/{context.IssueId}/artifacts", CancellationToken.None);

        // AC-ART-103: the agent/CLI surface calls IArtifactService directly, so resolving it here
        // and comparing proves the two surfaces order identically rather than merely similarly.
        using var scope = context.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactService>();
        var overService = await service.ListArtifactsAsync(new IssueId(context.IssueId), CancellationToken.None);

        Assert.Equal(3, overRest!.Count);
        Assert.Equal(overService.Select(artifact => artifact.Id), overRest.Select(artifact => artifact.Id));
    }

    [Fact]
    public async Task Attach_UnknownKind_ReturnsBadRequest()
    {
        await using var context = await ApiTestContext.CreateAsync();

        var response = await context.Client.PostAsJsonAsync(
            $"/api/issues/{context.IssueId}/artifacts",
            new { kind = "screenshot", title = "A screenshot", contentReference = "https://example.test/s.png", actorId = context.MemberId },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("VALIDATION_FAILED", payload.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Attach_UnknownIssue_ReturnsNotFound()
    {
        await using var context = await ApiTestContext.CreateAsync();

        var response = await context.Client.PostAsJsonAsync(
            $"/api/issues/{Guid.NewGuid()}/artifacts",
            new { kind = "link", title = "Anything", contentReference = "https://example.test", actorId = context.MemberId },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", payload.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Delete_ArtifactFromAnotherIssue_ReturnsNotFound()
    {
        await using var context = await ApiTestContext.CreateAsync();
        var otherIssueId = await context.CreateIssueAsync("Second issue");

        var attach = await context.Client.PostAsJsonAsync(
            $"/api/issues/{otherIssueId}/artifacts",
            new { kind = "link", title = "Theirs", contentReference = "https://example.test/b", actorId = context.MemberId },
            CancellationToken.None);
        var created = await attach.Content.ReadFromJsonAsync<ArtifactDto>(CancellationToken.None);

        var response = await context.Client.DeleteAsync(
            $"/api/issues/{context.IssueId}/artifacts/{created!.Id}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // AC-ART-109: the misrouted delete must not have removed it from the owning issue either.
        var stillThere = await context.Client.GetFromJsonAsync<List<ArtifactDto>>(
            $"/api/issues/{otherIssueId}/artifacts", CancellationToken.None);
        Assert.Single(stillThere!);
    }

    [Fact]
    public async Task Delete_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync(
            $"/api/issues/{Guid.NewGuid()}/artifacts/{Guid.NewGuid()}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attach_Contributor_IsAllowedViaAssignedIssuesPermission()
    {
        await using var context = await ApiTestContext.CreateAsync();
        using var contributorClient = context.Factory.CreateClient();
        var cookie = await context.Factory.SeedMemberAndGetSessionCookieAsync(contributorClient, "contributor", Role.Contributor);
        contributorClient.DefaultRequestHeaders.Add("Cookie", cookie);

        // A Contributor holds ReadWriteAssignedIssues but not ReadWriteIssues, so this only passes
        // if the route accepts either — the same pair the issue mutation routes accept (BR-ART-5).
        var response = await contributorClient.PostAsJsonAsync(
            $"/api/issues/{context.IssueId}/artifacts",
            new { kind = "link", title = "Mine", contentReference = "https://example.test", actorId = context.MemberId },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>A bootstrapped host with a team, workflow states, and one issue already created.</summary>
    private sealed class ApiTestContext : IAsyncDisposable
    {
        private ApiTestContext(ApiFactory factory, HttpClient client, Guid teamId, Guid issueId, Guid memberId)
        {
            Factory = factory;
            Client = client;
            TeamId = teamId;
            IssueId = issueId;
            MemberId = memberId;
        }

        public ApiFactory Factory { get; }
        public HttpClient Client { get; }
        public Guid TeamId { get; }
        public Guid IssueId { get; }
        public Guid MemberId { get; }

        public static async Task<ApiTestContext> CreateAsync()
        {
            var factory = new ApiFactory();
            var client = factory.CreateClient();
            var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            await factory.SeedWorkflowStatesAsync();

            var teamResponse = await client.PostAsJsonAsync(
                "/api/teams", new { name = "Test team", key = "tst" }, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Created, teamResponse.StatusCode);
            using var teamPayload = JsonDocument.Parse(await teamResponse.Content.ReadAsStringAsync(CancellationToken.None));
            var teamId = teamPayload.RootElement.GetProperty("id").GetGuid();

            Guid memberId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
                memberId = (await db.Members.AsNoTracking().Select(member => member.Id).FirstAsync()).Value;
            }

            var context = new ApiTestContext(factory, client, teamId, Guid.Empty, memberId);
            var issueId = await context.CreateIssueAsync("First issue");
            return new ApiTestContext(factory, client, teamId, issueId, memberId);
        }

        public async Task<Guid> CreateIssueAsync(string title)
        {
            var response = await Client.PostAsJsonAsync(
                "/api/issues", new { teamId = TeamId, title }, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            return payload.RootElement.GetProperty("id").GetGuid();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
