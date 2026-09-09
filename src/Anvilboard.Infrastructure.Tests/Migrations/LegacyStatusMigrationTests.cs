using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Infrastructure.Tests.Migrations;

public sealed class LegacyStatusMigrationTests
{
    [Fact]
    public async Task LegacyStatusMigration_SeedsWorkflowAndBackfillsIssueStates()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";

        try
        {
            await SeedLegacyDatabaseAsync(connectionString);

            var options = new DbContextOptionsBuilder<AnvilboardDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var db = new AnvilboardDbContext(options))
            {
                await db.Database.MigrateAsync();

                var states = await db.WorkflowStates.OrderBy(state => state.Order).ToListAsync();
                Assert.Collection(
                    states,
                    state => AssertState(state, "backlog", "Backlog", 0, false),
                    state => AssertState(state, "todo", "Todo", 1, false),
                    state => AssertState(state, "in_progress", "In Progress", 2, false),
                    state => AssertState(state, "in_review", "In Review", 3, false),
                    state => AssertState(state, "done", "Done", 4, true),
                    state => AssertState(state, "cancelled", "Cancelled", 5, true));

                var expectedKeys = new[] { "backlog", "todo", "in_progress", "in_review", "done", "cancelled" };
                var issues = await db.Issues.OrderBy(issue => issue.Status).ToListAsync();
                Assert.Equal(expectedKeys.Length, issues.Count);
                foreach (var issue in issues)
                {
                    Assert.NotNull(issue.WorkflowStateId);
                    Assert.Equal(expectedKeys[(int)issue.Status],
                        states.Single(state => state.Id == issue.WorkflowStateId).Key);
                }

                Assert.Equal(9, await db.WorkflowTransitions.CountAsync());
            }
        }
        finally
        {
            // SQLite connection pooling can retain a handle briefly after disposal on Windows.
            // The uniquely named test database is safe to leave for OS temp-file cleanup.
            SqliteConnection.ClearAllPools();
            TryDeleteDatabase(databasePath);
        }
    }

    private static void AssertState(WorkflowState state, string key, string displayName, int order, bool isTerminal)
    {
        Assert.Equal(key, state.Key);
        Assert.Equal(displayName, state.DisplayName);
        Assert.Equal(order, state.Order);
        Assert.Equal(isTerminal, state.IsTerminal);
        Assert.False(state.IsArchived);
    }

    private static void TryDeleteDatabase(string databasePath)
    {
        try
        {
            File.Delete(databasePath);
        }
        catch (IOException)
        {
            // A locked uniquely named temp file has no effect on subsequent test runs.
        }
    }

    private static async Task SeedLegacyDatabaseAsync(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Workspaces (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Slug TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                CREATE TABLE Teams (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, Name TEXT NOT NULL, Key TEXT NOT NULL, NextIssueNumber INTEGER NOT NULL, CreatedAt TEXT NOT NULL);
                CREATE TABLE Issues (Id TEXT NOT NULL PRIMARY KEY, TeamId TEXT NOT NULL, ProjectId TEXT NULL, Key TEXT NOT NULL, Title TEXT NOT NULL, Description TEXT NULL, Status INTEGER NOT NULL, Priority INTEGER NOT NULL, AssigneeId TEXT NULL, CreatedById TEXT NULL, Source INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, CompletedAt TEXT NULL, LabelIds TEXT NOT NULL);
                CREATE TABLE Members (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, DisplayName TEXT NOT NULL, Email TEXT NULL, AvatarUrl TEXT NULL, IsAgent INTEGER NOT NULL);
                CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();

            var workspaceId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            await ExecuteAsync(connection,
                "INSERT INTO Workspaces (Id, Name, Slug, CreatedAt) VALUES ($id, 'Legacy workspace', 'legacy-workspace', $now)",
                ("$id", workspaceId.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            await ExecuteAsync(connection,
                "INSERT INTO Teams (Id, WorkspaceId, Name, Key, NextIssueNumber, CreatedAt) VALUES ($id, $workspaceId, 'Legacy team', 'LEG', 7, $now)",
                ("$id", teamId.ToString()), ("$workspaceId", workspaceId.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")));

            for (var status = 0; status <= 5; status++)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO Issues (Id, TeamId, Key, Title, Status, Priority, Source, CreatedAt, UpdatedAt, LabelIds)
                    VALUES ($id, $teamId, $key, 'Legacy issue', $status, 0, 0, $now, $now, '[]')
                    """,
                    ("$id", Guid.NewGuid().ToString()), ("$teamId", teamId.ToString()), ("$key", $"LEG-{status + 1}"),
                    ("$status", status), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            }

            await ExecuteAsync(connection,
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260904192510_InitialCreate', '10.0.11')");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
