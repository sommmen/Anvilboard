using Anvilboard.Application.Issues;
using Anvilboard.Application.Workflows;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anvilboard.Application.Tests.Workflows;

public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task CreateAsync_AssignsLowestOrderedActiveWorkflowState()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var laterState = fixture.CreateState("todo", "Todo", order: 1);
        await fixture.Db.SaveChangesAsync();

        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);
        var issue = await service.CreateAsync(fixture.TeamId, "Plan v0.1");

        Assert.Equal(fixture.Current.Id, issue.WorkflowStateId);
        Assert.NotEqual(laterState.Id, issue.WorkflowStateId);
    }

    [Fact]
    public async Task UpsertFromExternalAsync_AssignsLowestOrderedActiveWorkflowState()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);
        var normalized = new NormalizedIssue(
            IntegrationProvider.GitHub,
            "github-42",
            "TST",
            "Synced issue",
            null,
            IssueStatus.Backlog,
            IssuePriority.None,
            null,
            null,
            [],
            null,
            DateTimeOffset.UtcNow);

        var issue = await service.UpsertFromExternalAsync(normalized);

        Assert.Equal(fixture.Current.Id, issue.WorkflowStateId);
    }

    [Fact]
    public async Task CreateAsync_WithoutActiveWorkflowState_Throws()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Current.IsArchived = true;
        await fixture.Db.SaveChangesAsync();
        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(fixture.TeamId, "No available state"));

        Assert.Contains("no active workflow state", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTransitionAsync_ConfiguredTransition_ReturnsAllowed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var target = fixture.CreateState("todo", "Todo");
        fixture.Db.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = fixture.WorkspaceId,
            FromStateId = fixture.Current.Id,
            ToStateId = target.Id,
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Engine.ValidateTransitionAsync(fixture.WorkspaceId, fixture.Current.Id, target.Id);

        Assert.True(result.IsAllowed);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task ValidateTransitionAsync_UnconfiguredTransition_ReturnsInvalidWorkflowTransition()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var target = fixture.CreateState("done", "Done", isTerminal: true);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Engine.ValidateTransitionAsync(fixture.WorkspaceId, fixture.Current.Id, target.Id);

        Assert.False(result.IsAllowed);
        Assert.Equal("INVALID_WORKFLOW_TRANSITION", result.ErrorCode);
        Assert.Contains("current", result.Message, StringComparison.Ordinal);
        Assert.Contains("done", result.Message, StringComparison.Ordinal);
        Assert.Contains("configured transition rule", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTransitionAsync_UnknownState_ReturnsReferencedEntityNotFound()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var missing = WorkflowStateId.New();

        var result = await fixture.Engine.ValidateTransitionAsync(fixture.WorkspaceId, missing, fixture.Current.Id);

        Assert.False(result.IsAllowed);
        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", result.ErrorCode);
        Assert.Contains(missing.ToString(), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateTransitionAsync_SameState_ReturnsAllowedNoOp()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var result = await fixture.Engine.ValidateTransitionAsync(
            fixture.WorkspaceId, fixture.Current.Id, fixture.Current.Id);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CreateWorkflowStateAsync_DuplicateKey_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowStateAsync(fixture.WorkspaceId, "current", "Current", 1, false));

        Assert.Equal("VALIDATION_FAILED", WorkflowValidationException.ErrorCode);
        Assert.Contains("current", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Db.WorkflowStates.CountAsync());
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_DependentIssueWithoutReplacement_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Issues.Add(fixture.CreateIssue(fixture.Current.Id));
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.ArchiveWorkflowStateAsync(fixture.WorkspaceId, fixture.Current.Id, null));

        Assert.False((await fixture.Db.WorkflowStates.SingleAsync()).IsArchived);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_DependentIssueWithActiveReplacement_ReassignsAndArchives()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var replacement = fixture.CreateState("todo", "Todo");
        var issue = fixture.CreateIssue(fixture.Current.Id);
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.ArchiveWorkflowStateAsync(fixture.WorkspaceId, fixture.Current.Id, replacement.Id);

        Assert.True((await fixture.Db.WorkflowStates.SingleAsync(state => state.Id == fixture.Current.Id)).IsArchived);
        Assert.Equal(replacement.Id, (await fixture.Db.Issues.SingleAsync()).WorkflowStateId);
    }

    [Fact]
    public async Task ChangeStatusAsync_AllowedTransition_UpdatesWorkflowStateVersionAndStatus()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var todo = fixture.CreateState("todo", "Todo", order: 1);
        fixture.Db.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = fixture.WorkspaceId,
            FromStateId = backlog.Id,
            ToStateId = todo.Id,
        });
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Status = IssueStatus.Backlog;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);
        var updated = await service.ChangeStatusAsync(issue.Id, IssueStatus.Todo);

        Assert.Equal(IssueStatus.Todo, updated.Status);
        Assert.Equal(todo.Id, updated.WorkflowStateId);
        Assert.Equal(1, updated.Version);
        Assert.Null(updated.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatusAsync_DisallowedTransition_ThrowsWorkflowTransitionDeniedAndLeavesIssueUnchanged()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Status = IssueStatus.Backlog;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);

        var exception = await Assert.ThrowsAsync<WorkflowTransitionDeniedException>(
            () => service.ChangeStatusAsync(issue.Id, IssueStatus.Done));

        Assert.Equal("INVALID_WORKFLOW_TRANSITION", exception.ErrorCode);
        var unchanged = await fixture.Db.Issues.AsNoTracking().SingleAsync(i => i.Id == issue.Id);
        Assert.Equal(IssueStatus.Backlog, unchanged.Status);
        Assert.Equal(backlog.Id, unchanged.WorkflowStateId);
        Assert.Equal(0, unchanged.Version);
    }

    [Fact]
    public async Task ChangeStatusAsync_SameStatus_IsNoOpAndDoesNotCallWorkflowService()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Status = IssueStatus.Backlog;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = new IssueService(fixture.Db, new FakePluginRegistry(), fixture.Engine, NullLogger<IssueService>.Instance);
        var result = await service.ChangeStatusAsync(issue.Id, IssueStatus.Backlog);

        Assert.Equal(0, result.Version);
        Assert.Equal(backlog.Id, result.WorkflowStateId);
    }

    private sealed class FakePluginRegistry : IPluginRegistry
    {
        public IReadOnlyList<IAnvilboardPlugin> All { get; } = [];
        public IReadOnlyList<IIngestionSource> IngestionSources { get; } = [];
        public IReadOnlyList<IWebhookReceiver> WebhookReceivers { get; } = [];
        public IReadOnlyList<IIssueHook> IssueHooks { get; } = [];
    }

    private sealed class WorkflowFixture : IAsyncDisposable
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
            Engine = new WorkflowEngine(db);
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }
        public TeamId TeamId { get; }
        public WorkflowState Current { get; }
        public WorkflowEngine Engine { get; }

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
}
