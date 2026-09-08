using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Issues;

public sealed class IssueLinkServiceTests
{
    [Fact]
    public async Task CreateLinkAsync_PersistsDirectionalLinkAndActivityEvent()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var link = await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED", "same parent");

        Assert.Equal(fixture.IssueA.Id.Value, link.SourceIssueId);
        Assert.Equal(fixture.IssueB.Id.Value, link.TargetIssueId);
        Assert.Equal("RELATED", link.Type);
        Assert.Equal("same parent", link.Description);
        Assert.Equal(IssueLinkDirection.Outgoing, link.Direction);

        var stored = await fixture.Db.IssueLinks.SingleAsync();
        Assert.Equal(fixture.IssueA.Id, stored.SourceIssueId);
        Assert.Equal(fixture.IssueB.Id, stored.TargetIssueId);

        var activity = await fixture.Db.ActivityEvents.SingleAsync();
        Assert.Equal(ActivityEventType.IssueLinkCreated, activity.Type);
        Assert.Equal(fixture.IssueA.Id, activity.IssueId);
    }

    [Fact]
    public async Task CreateLinkAsync_UnlistedTypeIsAcceptedVerbatim()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var link = await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "CUSTOM_TYPE");

        Assert.Equal("CUSTOM_TYPE", link.Type);
    }

    [Fact]
    public async Task CreateLinkAsync_DescriptionDefaultsToEmptyString()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var link = await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "PARENT");

        Assert.Equal(string.Empty, link.Description);
    }

    [Fact]
    public async Task CreateLinkAsync_SelfLink_ThrowsValidationFailed()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueA.Id, "RELATED"));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateLinkAsync_BlankType_ThrowsValidationFailed()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "   "));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateLinkAsync_MissingTargetIssue_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.CreateLinkAsync(fixture.IssueA.Id, IssueId.New(), "RELATED"));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateLinkAsync_CrossWorkspaceIssues_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueOtherWorkspace.Id, "RELATED"));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateLinkAsync_DirectionalDuplicate_ThrowsResourceAlreadyExists()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED"));

        Assert.Equal("RESOURCE_ALREADY_EXISTS", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateLinkAsync_InverseDirection_IsPermitted()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");

        var reverse = await service.CreateLinkAsync(fixture.IssueB.Id, fixture.IssueA.Id, "RELATED");

        Assert.Equal(fixture.IssueB.Id.Value, reverse.SourceIssueId);
        Assert.Equal(2, await fixture.Db.IssueLinks.CountAsync());
    }

    [Fact]
    public async Task ListLinksAsync_ReturnsLinksForBothEndpointsWithComputedDirection()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");
        await service.CreateLinkAsync(fixture.IssueC.Id, fixture.IssueA.Id, "BLOCKS");

        var links = await service.ListLinksAsync(fixture.IssueA.Id);

        Assert.Equal(2, links.Count);
        var outgoing = Assert.Single(links, l => l.Type == "RELATED");
        Assert.Equal(IssueLinkDirection.Outgoing, outgoing.Direction);
        var incoming = Assert.Single(links, l => l.Type == "BLOCKS");
        Assert.Equal(IssueLinkDirection.Incoming, incoming.Direction);
    }

    [Fact]
    public async Task ListLinksAsync_OrdersByCreatedAtAscending()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");
        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueC.Id, "DUPLICATE");

        var links = await service.ListLinksAsync(fixture.IssueA.Id);

        Assert.True(links[0].CreatedAt <= links[1].CreatedAt);
    }

    [Fact]
    public async Task RemoveLinkAsync_DeletesLinkAndRecordsActivityEvent()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        var link = await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");

        await service.RemoveLinkAsync(fixture.IssueA.Id, new IssueLinkId(link.Id));

        Assert.Empty(await fixture.Db.IssueLinks.ToListAsync());
        var removalEvent = await fixture.Db.ActivityEvents.SingleAsync(e => e.Type == ActivityEventType.IssueLinkRemoved);
        Assert.Equal(fixture.IssueA.Id, removalEvent.IssueId);
    }

    [Fact]
    public async Task RemoveLinkAsync_LinkNotAssociatedWithIssue_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        var link = await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "RELATED");

        var ex = await Assert.ThrowsAsync<IssueLinkException>(
            () => service.RemoveLinkAsync(fixture.IssueC.Id, new IssueLinkId(link.Id)));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task BlocksType_NeverGatesPhaseTransition()
    {
        // BLOCKS is purely informational: creating one must not touch workflow state, version,
        // or status on either issue, and no workflow service is involved in this component at all.
        await using var fixture = await IssueLinkFixture.CreateAsync();
        var service = new IssueLinkService(fixture.Db);
        var beforeStatus = fixture.IssueB.Status;
        var beforeState = fixture.IssueB.WorkflowStateId;
        var beforeVersion = fixture.IssueB.Version;

        await service.CreateLinkAsync(fixture.IssueA.Id, fixture.IssueB.Id, "BLOCKS");

        var unchanged = await fixture.Db.Issues.AsNoTracking().SingleAsync(i => i.Id == fixture.IssueB.Id);
        Assert.Equal(beforeStatus, unchanged.Status);
        Assert.Equal(beforeState, unchanged.WorkflowStateId);
        Assert.Equal(beforeVersion, unchanged.Version);
    }

    private sealed class IssueLinkFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private IssueLinkFixture(SqliteConnection connection, AnvilboardDbContext db, Issue issueA, Issue issueB, Issue issueC, Issue issueOtherWorkspace)
        {
            this.connection = connection;
            Db = db;
            IssueA = issueA;
            IssueB = issueB;
            IssueC = issueC;
            IssueOtherWorkspace = issueOtherWorkspace;
        }

        public AnvilboardDbContext Db { get; }
        public Issue IssueA { get; }
        public Issue IssueB { get; }
        public Issue IssueC { get; }
        public Issue IssueOtherWorkspace { get; }

        public static async Task<IssueLinkFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var teamId = TeamId.New();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Test workspace", Slug = "test-workspace", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Test team", Key = "TST", CreatedAt = DateTimeOffset.UtcNow });

            var otherWorkspaceId = WorkspaceId.New();
            var otherTeamId = TeamId.New();
            db.Workspaces.Add(new Workspace { Id = otherWorkspaceId, Name = "Other workspace", Slug = "other-workspace", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = otherTeamId, WorkspaceId = otherWorkspaceId, Name = "Other team", Key = "OTH", CreatedAt = DateTimeOffset.UtcNow });

            Issue MakeIssue(TeamId team, string key) => new()
            {
                Id = IssueId.New(),
                TeamId = team,
                Key = key,
                Title = $"Issue {key}",
                Status = IssueStatus.Backlog,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var issueA = MakeIssue(teamId, "TST-1");
            var issueB = MakeIssue(teamId, "TST-2");
            var issueC = MakeIssue(teamId, "TST-3");
            var issueOtherWorkspace = MakeIssue(otherTeamId, "OTH-1");
            db.Issues.AddRange(issueA, issueB, issueC, issueOtherWorkspace);
            await db.SaveChangesAsync();

            return new IssueLinkFixture(connection, db, issueA, issueB, issueC, issueOtherWorkspace);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
