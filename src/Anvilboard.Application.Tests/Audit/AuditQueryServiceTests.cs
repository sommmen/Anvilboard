using Anvilboard.Application.Auditing;
using Anvilboard.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Application.Tests.Audit;

/// <summary>
/// Covers the MAJ-018 audit read path: newest-first ordering, cursor paging, workspace scoping,
/// every filter, limit validation, and the scan-ceiling guard. See
/// <c>docs/plans/audit-query-surface.md</c> for the design this verifies.
/// </summary>
public sealed class AuditQueryServiceTests
{
    [Fact]
    public async Task QueryAsync_ReturnsNewestFirst()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        fixture.AddEvent(start, action: "issue.create");
        fixture.AddEvent(start.AddMinutes(1), action: "issue.update");
        fixture.AddEvent(start.AddMinutes(2), action: "issue.delete");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery());

        Assert.Equal(
            ["issue.delete", "issue.update", "issue.create"],
            result.Events.Select(e => e.Action));
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task QueryAsync_ScopesToTheCallersWorkspace()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, action: "issue.create");
        fixture.AddForeignEvent(DateTimeOffset.UtcNow, action: "issue.create");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery());

        var entry = Assert.Single(result.Events);
        Assert.Equal("issue.create", entry.Action);
    }

    [Fact]
    public async Task QueryAsync_PagesThroughTheCursorWithoutRepeatingOrSkipping()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
        {
            fixture.AddEvent(start.AddMinutes(i), action: $"action-{i}");
        }

        await fixture.SaveAsync();

        var service = new AuditQueryService(fixture.Db);

        var first = await service.QueryAsync(fixture.WorkspaceId, new AuditQuery(Limit: 2));
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["action-4", "action-3"], first.Events.Select(e => e.Action));

        var second = await service.QueryAsync(
            fixture.WorkspaceId, new AuditQuery(Limit: 2, Cursor: first.NextCursor));
        Assert.True(second.HasMore);
        Assert.Equal(["action-2", "action-1"], second.Events.Select(e => e.Action));

        var third = await service.QueryAsync(
            fixture.WorkspaceId, new AuditQuery(Limit: 2, Cursor: second.NextCursor));
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);
        Assert.Equal(["action-0"], third.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task QueryAsync_FiltersByActorId()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, actorId: "user:ada", action: "issue.create");
        fixture.AddEvent(DateTimeOffset.UtcNow, actorId: "user:grace", action: "issue.update");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(ActorId: "user:ada"));

        var entry = Assert.Single(result.Events);
        Assert.Equal("user:ada", entry.ActorId);
    }

    [Fact]
    public async Task QueryAsync_FiltersByTargetTypeAndTargetId()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, targetType: "issue", targetId: "issue-1", action: "issue.create");
        fixture.AddEvent(DateTimeOffset.UtcNow, targetType: "issue", targetId: "issue-2", action: "issue.create");
        fixture.AddEvent(DateTimeOffset.UtcNow, targetType: "team", targetId: "team-1", action: "team.create");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(TargetType: "issue", TargetId: "issue-1"));

        var entry = Assert.Single(result.Events);
        Assert.Equal("issue", entry.TargetType);
        Assert.Equal("issue-1", entry.TargetId);
    }

    [Fact]
    public async Task QueryAsync_FiltersByAction()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, action: "issue.create");
        fixture.AddEvent(DateTimeOffset.UtcNow, action: "issue.delete");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(Action: "issue.delete"));

        var entry = Assert.Single(result.Events);
        Assert.Equal("issue.delete", entry.Action);
    }

    [Fact]
    public async Task QueryAsync_FiltersByChannel()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, channel: AuditChannel.Rest, action: "issue.create");
        fixture.AddEvent(DateTimeOffset.UtcNow, channel: AuditChannel.Cli, action: "issue.create");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(Channel: AuditChannel.Cli));

        var entry = Assert.Single(result.Events);
        Assert.Equal(AuditChannel.Cli, entry.Channel);
    }

    [Fact]
    public async Task QueryAsync_FiltersByOccurredAfterAndOccurredBefore()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        fixture.AddEvent(start, action: "too-early");
        fixture.AddEvent(start.AddMinutes(5), action: "in-range");
        fixture.AddEvent(start.AddMinutes(10), action: "too-late");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db).QueryAsync(
            fixture.WorkspaceId,
            new AuditQuery(OccurredAfter: start.AddMinutes(1), OccurredBefore: start.AddMinutes(9)));

        var entry = Assert.Single(result.Events);
        Assert.Equal("in-range", entry.Action);
    }

    [Fact]
    public async Task QueryAsync_EchoesTheResolvedLimitInTheAppliedQuery()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.AddEvent(DateTimeOffset.UtcNow, action: "issue.create");
        await fixture.SaveAsync();

        var result = await new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery());

        Assert.Equal(AuditQueryService.DefaultLimit, result.AppliedQuery.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task QueryAsync_LimitOutsideRange_Throws(int limit)
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.SaveAsync();

        var exception = await Assert.ThrowsAsync<AuditQueryException>(() => new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(Limit: limit)));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_OccurredAfterNotBeforeOccurredBefore_Throws()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.SaveAsync();
        var now = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<AuditQueryException>(() => new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(OccurredAfter: now, OccurredBefore: now.AddMinutes(-1))));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_MalformedCursor_Throws()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.SaveAsync();

        var exception = await Assert.ThrowsAsync<AuditQueryException>(() => new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery(Cursor: "not-a-valid-cursor!!")));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_MatchesMoreThanTheScanCeiling_Throws()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 0; i < AuditQueryService.ScanCeiling + 1; i++)
        {
            fixture.AddEvent(start.AddSeconds(i), action: $"bulk-{i}");
        }

        await fixture.SaveAsync();

        var exception = await Assert.ThrowsAsync<AuditQueryException>(() => new AuditQueryService(fixture.Db)
            .QueryAsync(fixture.WorkspaceId, new AuditQuery()));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private AuditFixture(SqliteConnection connection, AnvilboardDbContext db, WorkspaceId workspaceId, WorkspaceId foreignWorkspaceId)
        {
            this.connection = connection;
            Db = db;
            WorkspaceId = workspaceId;
            ForeignWorkspaceId = foreignWorkspaceId;
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }

        /// <summary>A second workspace, used to prove the read path is scoped.</summary>
        public WorkspaceId ForeignWorkspaceId { get; }

        public static async Task<AuditFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var foreignWorkspaceId = WorkspaceId.New();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Test", Slug = "test", CreatedAt = DateTimeOffset.UtcNow });
            db.Workspaces.Add(new Workspace { Id = foreignWorkspaceId, Name = "Other", Slug = "other", CreatedAt = DateTimeOffset.UtcNow });

            return new AuditFixture(connection, db, workspaceId, foreignWorkspaceId);
        }

        public void AddEvent(
            DateTimeOffset occurredAt,
            string actorId = "user:ada",
            AuditChannel channel = AuditChannel.Rest,
            string action = "issue.create",
            string targetType = "issue",
            string targetId = "issue-1",
            string correlationId = "corr-1")
        {
            Db.AuditEvents.Add(new AuditEvent
            {
                Id = AuditEventId.New(),
                WorkspaceId = WorkspaceId,
                ActorId = actorId,
                Channel = channel,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                CorrelationId = correlationId,
                OccurredAt = occurredAt,
                ResultSummary = "ok",
            });
        }

        public void AddForeignEvent(DateTimeOffset occurredAt, string action)
        {
            Db.AuditEvents.Add(new AuditEvent
            {
                Id = AuditEventId.New(),
                WorkspaceId = ForeignWorkspaceId,
                ActorId = "user:foreign",
                Channel = AuditChannel.Rest,
                Action = action,
                TargetType = "issue",
                TargetId = "issue-1",
                CorrelationId = "corr-foreign",
                OccurredAt = occurredAt,
                ResultSummary = "ok",
            });
        }

        public Task SaveAsync() => Db.SaveChangesAsync();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
