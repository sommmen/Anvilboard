using System.Text;
using Anvilboard.Application.Activity;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Activity;

/// <summary>
/// Covers AC-BXP-009: the activity read path returns newest-first entries, pages deterministically
/// through an opaque cursor, refuses to answer for an issue outside the caller's workspace, and
/// renders every event — including ones whose payload is unreadable.
/// </summary>
public sealed class ActivityQueryServiceTests
{
    [Fact]
    public async Task ListForIssueAsync_ReturnsNewestFirst()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.Created, start);
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.StatusChanged, start.AddMinutes(1));
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.CommentAdded, start.AddMinutes(2));

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        Assert.Equal(
            ["CommentAdded", "StatusChanged", "Created"],
            page.Entries.Select(entry => entry.Type));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListForIssueAsync_PagesThroughTheCursorWithoutRepeatingOrSkipping()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
        {
            await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.StatusChanged, start.AddMinutes(i));
        }

        var service = new ActivityQueryService(fixture.Db);

        var first = await service.ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, limit: 2);
        Assert.Equal(2, first.Entries.Count);
        Assert.NotNull(first.NextCursor);

        var second = await service.ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, limit: 2, cursor: first.NextCursor);
        Assert.Equal(2, second.Entries.Count);
        Assert.NotNull(second.NextCursor);

        var third = await service.ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, limit: 2, cursor: second.NextCursor);
        Assert.Single(third.Entries);
        Assert.Null(third.NextCursor);

        var seen = first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.Id).ToList();
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListForIssueAsync_OmitsEventsBelongingToOtherIssues()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.Created, now);
        await fixture.AddEventAsync(fixture.OtherIssue.Id, ActivityEventType.Created, now);

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        Assert.Single(page.Entries);
    }

    [Fact]
    public async Task ListForIssueAsync_ForeignWorkspaceIssueIsNotFound()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        await fixture.AddEventAsync(fixture.ForeignIssue.Id, ActivityEventType.Created, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<ActivityQueryException>(() => new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.ForeignIssue.Id));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ActivityQueryService.MaximumLimit + 1)]
    public async Task ListForIssueAsync_RejectsAnOutOfRangeLimit(int limit)
    {
        await using var fixture = await ActivityFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<ActivityQueryException>(() => new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, limit));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("b2Zmc2V0Oi01")] // offset:-5
    [InlineData("Z2FyYmFnZQ==")] // garbage
    public async Task ListForIssueAsync_RejectsAMalformedCursor(string cursor)
    {
        await using var fixture = await ActivityFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<ActivityQueryException>(() => new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, cursor: cursor));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task ListForIssueAsync_ResolvesTheActorDisplayNameAndRendersText()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        await fixture.AddEventAsync(
            fixture.Issue.Id, ActivityEventType.CommentAdded, DateTimeOffset.UtcNow, fixture.MemberId);

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(fixture.MemberId.Value, entry.ActorId);
        Assert.Equal("Ada", entry.ActorDisplayName);
        Assert.Equal("Ada commented", entry.Text);
    }

    [Fact]
    public async Task ListForIssueAsync_UnknownActorStillRendersAnEntry()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.StatusChanged, DateTimeOffset.UtcNow);

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        var entry = Assert.Single(page.Entries);
        Assert.Null(entry.ActorDisplayName);
        Assert.Equal("Someone changed the status", entry.Text);
    }

    [Fact]
    public async Task ListForIssueAsync_ExposesThePayloadAsStructuredData()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        await fixture.AddEventAsync(
            fixture.Issue.Id, ActivityEventType.StatusChanged, DateTimeOffset.UtcNow, dataJson: """{"to":"done"}""");

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        var entry = Assert.Single(page.Entries);
        Assert.NotNull(entry.Data);
        Assert.Equal("done", entry.Data!.Value.GetProperty("to").GetString());
    }

    [Fact]
    public async Task ListForIssueAsync_MalformedPayloadDoesNotDropTheEntry()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        await fixture.AddEventAsync(
            fixture.Issue.Id, ActivityEventType.StatusChanged, DateTimeOffset.UtcNow, dataJson: "{not json");

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id);

        var entry = Assert.Single(page.Entries);
        Assert.Null(entry.Data);
        Assert.Equal("Someone changed the status", entry.Text);
    }

    [Fact]
    public async Task ListForIssueAsync_CursorIsAnOpaqueOffsetToken()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow;
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.Created, start);
        await fixture.AddEventAsync(fixture.Issue.Id, ActivityEventType.StatusChanged, start.AddMinutes(1));

        var page = await new ActivityQueryService(fixture.Db)
            .ListForIssueAsync(fixture.WorkspaceId, fixture.Issue.Id, limit: 1);

        Assert.Equal("offset:1", Encoding.UTF8.GetString(Convert.FromBase64String(page.NextCursor!)));
    }

    private sealed class ActivityFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ActivityFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            WorkspaceId workspaceId,
            MemberId memberId,
            Issue issue,
            Issue otherIssue,
            Issue foreignIssue)
        {
            this.connection = connection;
            Db = db;
            WorkspaceId = workspaceId;
            MemberId = memberId;
            Issue = issue;
            OtherIssue = otherIssue;
            ForeignIssue = foreignIssue;
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }
        public MemberId MemberId { get; }
        public Issue Issue { get; }
        public Issue OtherIssue { get; }

        /// <summary>An issue in a second workspace, used to prove the read path is scoped.</summary>
        public Issue ForeignIssue { get; }

        public static async Task<ActivityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var teamId = TeamId.New();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Test", Slug = "test", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Team", Key = "TST", CreatedAt = DateTimeOffset.UtcNow });

            var memberId = MemberId.New();
            db.Members.Add(new Member
            {
                Id = memberId,
                WorkspaceId = workspaceId,
                DisplayName = "Ada",
                Username = "ada",
                Role = Role.Contributor,
            });

            var foreignWorkspaceId = WorkspaceId.New();
            var foreignTeamId = TeamId.New();
            db.Workspaces.Add(new Workspace { Id = foreignWorkspaceId, Name = "Other", Slug = "other", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = foreignTeamId, WorkspaceId = foreignWorkspaceId, Name = "Other team", Key = "OTH", CreatedAt = DateTimeOffset.UtcNow });

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

            var issue = MakeIssue(teamId, "TST-1");
            var otherIssue = MakeIssue(teamId, "TST-2");
            var foreignIssue = MakeIssue(foreignTeamId, "OTH-1");
            db.Issues.AddRange(issue, otherIssue, foreignIssue);
            await db.SaveChangesAsync();

            return new ActivityFixture(connection, db, workspaceId, memberId, issue, otherIssue, foreignIssue);
        }

        public async Task AddEventAsync(
            IssueId issueId,
            ActivityEventType type,
            DateTimeOffset occurredAt,
            MemberId? actorId = null,
            string? dataJson = null)
        {
            Db.ActivityEvents.Add(new ActivityEvent
            {
                Id = ActivityEventId.New(),
                IssueId = issueId,
                Type = type,
                ActorId = actorId,
                DataJson = dataJson,
                OccurredAt = occurredAt,
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
