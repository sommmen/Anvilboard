using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Issues;

public sealed class BoardQueryServiceTests
{
    [Fact]
    public async Task QueryAsync_AppliesTypeAndArchiveFiltersAndReturnsCursor()
    {
        await using var fixture = await BoardQueryFixture.CreateAsync();
        var service = new BoardQueryService(fixture.Db);

        var firstPage = await service.QueryAsync(new BoardQuery(fixture.WorkspaceId, Type: "Bug", GroupBy: BoardGroupBy.Type, Limit: 1));

        var group = Assert.Single(firstPage.Groups);
        Assert.Equal("Bug", group.DisplayName);
        Assert.Single(group.Issues);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await service.QueryAsync(new BoardQuery(fixture.WorkspaceId, Type: "Bug", Limit: 1, Cursor: firstPage.NextCursor));

        Assert.Single(Assert.Single(secondPage.Groups).Issues);
        var withArchived = await service.QueryAsync(new BoardQuery(fixture.WorkspaceId, IncludeArchived: true, GroupBy: BoardGroupBy.Type));
        Assert.Equal(3, withArchived.TotalCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task QueryAsync_RejectsInvalidLimit(int limit)
    {
        await using var fixture = await BoardQueryFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<BoardQueryException>(() => new BoardQueryService(fixture.Db)
            .QueryAsync(new BoardQuery(fixture.WorkspaceId, Limit: limit)));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_RejectsMalformedCursor()
    {
        await using var fixture = await BoardQueryFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<BoardQueryException>(() => new BoardQueryService(fixture.Db)
            .QueryAsync(new BoardQuery(fixture.WorkspaceId, Cursor: "not-a-cursor")));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    private sealed class BoardQueryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private BoardQueryFixture(SqliteConnection connection, AnvilboardDbContext db, WorkspaceId workspaceId)
        {
            this.connection = connection;
            Db = db;
            WorkspaceId = workspaceId;
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }

        public static async Task<BoardQueryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AnvilboardDbContext(new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var teamId = TeamId.New();
            var workflowStateId = WorkflowStateId.New();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Workspace", Slug = "workspace", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Team", Key = "TST", CreatedAt = DateTimeOffset.UtcNow });
            db.WorkflowStates.Add(new WorkflowState { Id = workflowStateId, WorkspaceId = workspaceId, Key = "todo", DisplayName = "Todo", Order = 0 });
            var now = DateTimeOffset.UtcNow;
            db.Issues.AddRange(
                new Issue { Id = IssueId.New(), TeamId = teamId, WorkflowStateId = workflowStateId, Key = "TST-1", Title = "Newest bug", Type = "Bug", CreatedAt = now, UpdatedAt = now },
                new Issue { Id = IssueId.New(), TeamId = teamId, WorkflowStateId = workflowStateId, Key = "TST-2", Title = "Older bug", Type = "Bug", CreatedAt = now.AddMinutes(-1), UpdatedAt = now },
                new Issue { Id = IssueId.New(), TeamId = teamId, WorkflowStateId = workflowStateId, Key = "TST-3", Title = "Archived", Type = "Task", ArchivedAt = now, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            return new BoardQueryFixture(connection, db, workspaceId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
