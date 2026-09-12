using Anvilboard.Application.Auditing;
using Anvilboard.Application.Workflows;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Workflows;

/// <summary>
/// In-memory SQLite workspace with one active workflow state, shared by every workflow test so the
/// engine is always exercised against the real schema's unique indexes and FK shape rather than a
/// provider that silently tolerates duplicates.
/// </summary>
internal sealed class WorkflowFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;

    private WorkflowFixture(
        SqliteConnection connection,
        AnvilboardDbContext db,
        WorkspaceId workspaceId,
        TeamId teamId,
        WorkflowState current)
    {
        this.connection = connection;
        Db = db;
        WorkspaceId = workspaceId;
        TeamId = teamId;
        Current = current;
        Engine = new WorkflowEngine(db, new AuditService(db));
    }

    public AnvilboardDbContext Db { get; }
    public WorkspaceId WorkspaceId { get; }
    public TeamId TeamId { get; }
    public WorkflowState Current { get; }
    public WorkflowEngine Engine { get; }

    public WorkflowOperationContext Operation { get; } =
        new("member:test", AuditChannel.Cli, "corr-test");

    public static async Task<WorkflowFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
        var db = new AnvilboardDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var workspaceId = WorkspaceId.New();
        var current = new WorkflowState
        {
            Id = WorkflowStateId.New(),
            WorkspaceId = workspaceId,
            Key = "current",
            DisplayName = "Current",
            Order = 0,
        };
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Test workspace",
            Slug = "test-workspace",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var teamId = TeamId.New();
        db.Teams.Add(new Team
        {
            Id = teamId,
            WorkspaceId = workspaceId,
            Name = "Test team",
            Key = "TST",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.WorkflowStates.Add(current);
        await db.SaveChangesAsync();
        return new WorkflowFixture(connection, db, workspaceId, teamId, current);
    }

    public WorkflowState CreateState(string key, string displayName, int order = 1, bool isTerminal = false)
    {
        var state = new WorkflowState
        {
            Id = WorkflowStateId.New(),
            WorkspaceId = WorkspaceId,
            Key = key,
            DisplayName = displayName,
            Order = order,
            IsTerminal = isTerminal,
        };
        Db.WorkflowStates.Add(state);
        return state;
    }

    public WorkflowTransition CreateTransition(WorkflowStateId from, WorkflowStateId to)
    {
        var transition = new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = WorkspaceId,
            FromStateId = from,
            ToStateId = to,
        };
        Db.WorkflowTransitions.Add(transition);
        return transition;
    }

    public Task<List<AuditEvent>> AuditEventsAsync(string action) => Db.AuditEvents
        .AsNoTracking()
        .Where(e => e.Action == action)
        .ToListAsync();

    public Issue CreateIssue(WorkflowStateId workflowStateId) => new()
    {
        Id = IssueId.New(),
        TeamId = TeamId,
        Key = "TST-1",
        Title = "Test issue",
        Status = IssueStatus.Backlog,
        WorkflowStateId = workflowStateId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }
}
