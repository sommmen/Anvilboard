using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Domain;

namespace Anvilboard.Api.Tests.Authorization;

/// <summary>
/// Proves MAJ-022: every id-addressed REST route resolves ids inside the caller's own workspace, so
/// a session in workspace A can neither read nor mutate anything in workspace B.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs two workspaces on one host. That is the only arrangement that can fail: a
/// single-tenant host makes an unscoped query and a correctly scoped one indistinguishable, which
/// is precisely why the original leak survived a green suite.
/// </para>
/// <para>
/// Denials assert <c>403 WORKSPACE_ACCESS_DENIED</c> rather than <c>404</c> on purpose. A 404 for
/// "no such issue" next to a 403 for "not yours" would let an attacker enumerate which ids exist in
/// other tenants, so both cases share one indistinguishable response (AC-308).
/// </para>
/// <para>
/// Write routes additionally assert that nothing changed. A denied request that still committed
/// would be a leak even though its status code looked correct.
/// </para>
/// </remarks>
public sealed class CrossWorkspaceIsolationEndpointTests
{
    private const string OtherWorkspaceSlug = "other-workspace";
    private const string OtherAdministrator = "other-admin";

    [Fact]
    public async Task GetIssue_FromAnotherWorkspace_IsDeniedIndistinguishablyFromAnUnknownId()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var foreign = await client.GetAsync($"/api/issues/{foreignIssueId}", CancellationToken.None);
        var unknown = await client.GetAsync($"/api/issues/{Guid.NewGuid()}", CancellationToken.None);

        await AssertDeniedAsync(foreign);
        await AssertDeniedAsync(unknown);
        Assert.Equal(
            await foreign.Content.ReadAsStringAsync(CancellationToken.None),
            await unknown.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListIssues_NeverIncludesAnotherWorkspacesIssues()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync("/api/issues", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.DoesNotContain(
            payload.RootElement.EnumerateArray(),
            issue => issue.GetProperty("id").GetGuid() == foreignIssueId);
    }

    [Fact]
    public async Task ListIssues_FilteredByAnotherWorkspacesTeam_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (foreignTeamId, _) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync($"/api/issues?teamId={foreignTeamId}", CancellationToken.None);

        // An empty 200 would leak just as much as the issues themselves: it confirms the id exists.
        await AssertDeniedAsync(response);
    }

    [Fact]
    public async Task CreateIssue_IntoAnotherWorkspacesTeam_IsDeniedAndCreatesNothing()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (foreignTeamId, _) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/issues",
            new { teamId = foreignTeamId, title = "Planted" },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(1, await factory.CountIssuesInTeamAsync(foreignTeamId));
    }

    [Fact]
    public async Task ChangeStatus_OnAnotherWorkspacesIssue_IsDeniedAndLeavesTheStatusAlone()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{foreignIssueId}/status",
            // IssueStatus keeps the default numeric wire format; only Role/Permission are symbolic.
            new { status = (int)IssueStatus.Done },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(IssueStatus.Backlog, (await factory.ReadIssueAsync(foreignIssueId)).Status);
    }

    [Fact]
    public async Task Assign_OnAnotherWorkspacesIssue_IsDeniedAndLeavesTheAssigneeAlone()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{foreignIssueId}/assignee",
            new { assigneeId = (Guid?)null },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Null((await factory.ReadIssueAsync(foreignIssueId)).AssigneeId);
    }

    [Fact]
    public async Task AddComment_OnAnotherWorkspacesIssue_IsDeniedAndWritesNoComment()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/issues/{foreignIssueId}/comments",
            new { body = "Leaked" },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(0, (await factory.CountChildRowsAsync(foreignIssueId)).Comments);
    }

    [Fact]
    public async Task ListLinks_OnAnotherWorkspacesIssue_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync($"/api/issues/{foreignIssueId}/links", CancellationToken.None);

        await AssertDeniedAsync(response);
    }

    [Fact]
    public async Task CreateLink_BetweenTwoOfAnotherWorkspacesIssues_IsDeniedAndWritesNoLink()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var otherWorkspaceId = await factory.SeedAdditionalWorkspaceAsync(OtherWorkspaceSlug, OtherAdministrator);
        var (_, source) = await factory.SeedTeamWithIssueAsync(otherWorkspaceId, "FOR");
        var (_, target) = await factory.SeedTeamWithIssueAsync(otherWorkspaceId, "FOB");

        var response = await client.PostAsJsonAsync(
            $"/api/issues/{source}/links",
            new { targetIssueId = target, type = "RELATED" },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(0, (await factory.CountChildRowsAsync(source)).Links);
    }

    [Fact]
    public async Task RemoveLink_FromAnotherWorkspacesIssue_IsDeniedAndLeavesTheLinkIntact()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var otherWorkspaceId = await factory.SeedAdditionalWorkspaceAsync(OtherWorkspaceSlug, OtherAdministrator);
        var (_, source) = await factory.SeedTeamWithIssueAsync(otherWorkspaceId, "FOR");
        var (_, target) = await factory.SeedTeamWithIssueAsync(otherWorkspaceId, "FOB");
        var linkId = await factory.SeedLinkAsync(source, target);

        var response = await client.DeleteAsync($"/api/issues/{source}/links/{linkId}", CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(1, (await factory.CountChildRowsAsync(source)).Links);
    }

    [Fact]
    public async Task ListArtifacts_OnAnotherWorkspacesIssue_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync($"/api/issues/{foreignIssueId}/artifacts", CancellationToken.None);

        await AssertDeniedAsync(response);
    }

    [Fact]
    public async Task AttachArtifact_ToAnotherWorkspacesIssue_IsDeniedAndAttachesNothing()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/issues/{foreignIssueId}/artifacts",
            new { kind = "link", title = "Leaked", contentReference = "https://example.test" },
            CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(0, (await factory.CountChildRowsAsync(foreignIssueId)).Artifacts);
    }

    [Fact]
    public async Task RemoveArtifact_FromAnotherWorkspacesIssue_IsDeniedAndLeavesTheArtifactIntact()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (_, foreignIssueId) = await SeedForeignWorkspaceAsync(factory);
        var artifactId = await factory.SeedArtifactAsync(foreignIssueId);

        var response = await client.DeleteAsync(
            $"/api/issues/{foreignIssueId}/artifacts/{artifactId}", CancellationToken.None);

        await AssertDeniedAsync(response);
        Assert.Equal(1, (await factory.CountChildRowsAsync(foreignIssueId)).Artifacts);
    }

    [Fact]
    public async Task DashboardSummary_CountsOnlyTheCallersOwnWorkspace()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync("/api/dashboard/summary", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        // The foreign workspace owns exactly one Backlog issue, so an unscoped aggregate would show
        // up here as a non-zero bucket even though the caller's own workspace is empty.
        Assert.All(
            payload.RootElement.GetProperty("issuesByStatus").EnumerateObject(),
            bucket => Assert.Equal(0, bucket.Value.GetInt32()));
        Assert.Equal(0, payload.RootElement.GetProperty("createdLast7Days").GetInt32());
        Assert.Empty(payload.RootElement.GetProperty("openIssuesByAssignee").EnumerateArray());
    }

    [Fact]
    public async Task DashboardSummary_ScopedToAnotherWorkspacesTeam_IsDenied()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateCallerClientAsync(factory);
        var (foreignTeamId, _) = await SeedForeignWorkspaceAsync(factory);

        var response = await client.GetAsync(
            $"/api/dashboard/summary?teamId={foreignTeamId}", CancellationToken.None);

        await AssertDeniedAsync(response);
    }

    private static async Task<HttpClient> CreateCallerClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }

    private static async Task<(Guid TeamId, Guid IssueId)> SeedForeignWorkspaceAsync(ApiFactory factory)
    {
        var otherWorkspaceId = await factory.SeedAdditionalWorkspaceAsync(OtherWorkspaceSlug, OtherAdministrator);
        return await factory.SeedTeamWithIssueAsync(otherWorkspaceId);
    }

    private static async Task AssertDeniedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("WORKSPACE_ACCESS_DENIED", problem.RootElement.GetProperty("title").GetString());

        // The denial must not leak which of "absent" or "foreign" produced it.
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
    }
}
