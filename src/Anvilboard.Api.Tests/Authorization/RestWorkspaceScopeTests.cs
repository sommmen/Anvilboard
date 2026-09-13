using Anvilboard.Api.Authorization;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Tests.Authorization;

/// <summary>
/// Unit-level coverage of the REST boundary guard. The endpoint tests prove the routes call it;
/// these prove it answers correctly for each identifier kind, including the case the endpoint tests
/// cannot easily stage — a request resolved outside any HTTP context.
/// </summary>
public sealed class RestWorkspaceScopeTests
{
    [Fact]
    public async Task RequireTeamAsync_TeamInTheCallersWorkspace_ReturnsTheId()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        var id = await fixture.Scope.RequireTeamAsync(fixture.TeamId.Value);

        Assert.Equal(fixture.TeamId, id);
    }

    [Fact]
    public async Task RequireTeamAsync_TeamInAnotherWorkspace_Throws()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkspaceScopeDeniedException>(
            () => fixture.Scope.RequireTeamAsync(fixture.ForeignTeamId.Value));
    }

    [Fact]
    public async Task RequireTeamAsync_UnknownTeam_ThrowsTheSameWayAsAForeignOne()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkspaceScopeDeniedException>(
            () => fixture.Scope.RequireTeamAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RequireIssueAsync_IssueInTheCallersWorkspace_ReturnsTheId()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        var id = await fixture.Scope.RequireIssueAsync(fixture.IssueId.Value);

        Assert.Equal(fixture.IssueId, id);
    }

    [Fact]
    public async Task RequireIssueAsync_IssueInAnotherWorkspace_Throws()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkspaceScopeDeniedException>(
            () => fixture.Scope.RequireIssueAsync(fixture.ForeignIssueId.Value));
    }

    [Fact]
    public async Task RequireIssueAsync_UnknownIssue_ThrowsTheSameWayAsAForeignOne()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkspaceScopeDeniedException>(
            () => fixture.Scope.RequireIssueAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RequireMemberAsync_Null_ReturnsNullWithoutQuerying()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        Assert.Null(await fixture.Scope.RequireMemberAsync(null));
    }

    [Fact]
    public async Task RequireMemberAsync_MemberInAnotherWorkspace_Throws()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkspaceScopeDeniedException>(
            () => fixture.Scope.RequireMemberAsync(fixture.ForeignMemberId.Value));
    }

    [Fact]
    public async Task RequireMemberAsync_MemberInTheCallersWorkspace_ReturnsTheId()
    {
        await using var fixture = await ScopeFixture.CreateAsync();

        Assert.Equal(fixture.MemberId, await fixture.Scope.RequireMemberAsync(fixture.MemberId.Value));
    }

    [Fact]
    public async Task WorkspaceId_ResolvedOutsideARequest_ThrowsInsteadOfSilentlyScopingToNothing()
    {
        await using var fixture = await ScopeFixture.CreateAsync(withHttpContext: false);

        // Failing loudly matters: a scope that quietly produced a default WorkspaceId would filter
        // every query to a workspace that cannot exist, turning a misuse into silent empty results.
        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Scope.WorkspaceId);
        Assert.Contains("outside an HTTP request", ex.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class ScopeFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly AnvilboardDbContext db;

        private ScopeFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            RestWorkspaceScope scope,
            TeamId teamId,
            IssueId issueId,
            MemberId memberId,
            TeamId foreignTeamId,
            IssueId foreignIssueId,
            MemberId foreignMemberId)
        {
            this.connection = connection;
            this.db = db;
            Scope = scope;
            TeamId = teamId;
            IssueId = issueId;
            MemberId = memberId;
            ForeignTeamId = foreignTeamId;
            ForeignIssueId = foreignIssueId;
            ForeignMemberId = foreignMemberId;
        }

        public RestWorkspaceScope Scope { get; }

        public TeamId TeamId { get; }

        public IssueId IssueId { get; }

        public MemberId MemberId { get; }

        public TeamId ForeignTeamId { get; }

        public IssueId ForeignIssueId { get; }

        public MemberId ForeignMemberId { get; }

        public static async Task<ScopeFixture> CreateAsync(bool withHttpContext = true)
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var db = new AnvilboardDbContext(
                new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var (workspaceId, teamId, issueId, memberId) = await SeedWorkspaceAsync(db, "caller", "CAL");
            var (_, foreignTeamId, foreignIssueId, foreignMemberId) =
                await SeedWorkspaceAsync(db, "foreign", "FOR");

            // A plain holder rather than the framework's HttpContextAccessor: that one stores the
            // context in an AsyncLocal, which does not flow back out of this async factory method.
            var accessor = new StubHttpContextAccessor();
            if (withHttpContext)
            {
                var httpContext = new DefaultHttpContext();
                httpContext.Items[WorkspaceAuthorizationMiddleware.ActorContextItemsKey] =
                    new ActorContext(memberId, workspaceId, Role.Administrator);
                accessor.HttpContext = httpContext;
            }

            return new ScopeFixture(
                connection,
                db,
                new RestWorkspaceScope(db, accessor),
                teamId,
                issueId,
                memberId,
                foreignTeamId,
                foreignIssueId,
                foreignMemberId);
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static async Task<(WorkspaceId, TeamId, IssueId, MemberId)> SeedWorkspaceAsync(
            AnvilboardDbContext db, string slug, string teamKey)
        {
            var workspace = new Workspace
            {
                Id = WorkspaceId.New(),
                Name = slug,
                Slug = slug,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Workspaces.Add(workspace);

            var team = new Team
            {
                Id = TeamId.New(),
                WorkspaceId = workspace.Id,
                Name = teamKey,
                Key = teamKey,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Teams.Add(team);

            var member = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspace.Id,
                DisplayName = slug,
                Email = $"{slug}@example.test",
                Username = slug,
                PasswordHash = "unused",
                Role = Role.Administrator,
            };
            db.Members.Add(member);

            var issue = new Issue
            {
                Id = IssueId.New(),
                TeamId = team.Id,
                Key = $"{teamKey}-1",
                Title = $"{slug} issue",
                Status = IssueStatus.Backlog,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Issues.Add(issue);

            await db.SaveChangesAsync();
            return (workspace.Id, team.Id, issue.Id, member.Id);
        }
    }
}
