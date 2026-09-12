using Anvilboard.Application.Auditing;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Realtime;
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

        var service = CreateIssueService(fixture);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Plan v0.1");

        Assert.Equal(fixture.Current.Id, issue.WorkflowStateId);
        Assert.NotEqual(laterState.Id, issue.WorkflowStateId);
    }

    [Fact]
    public async Task UpsertFromExternalAsync_AssignsLowestOrderedActiveWorkflowState()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var service = CreateIssueService(fixture);
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

        var issue = await service.UpsertFromExternalUnscopedAsync(normalized);

        Assert.Equal(fixture.Current.Id, issue.WorkflowStateId);
    }

    [Fact]
    public async Task CreateAsync_WithoutActiveWorkflowState_Throws()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Current.IsArchived = true;
        await fixture.Db.SaveChangesAsync();
        var service = CreateIssueService(fixture);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "No available state"));

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
    public async Task CreateWorkflowStateAsync_DuplicateKey_ThrowsResourceAlreadyExists()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowStateAsync(
                fixture.WorkspaceId, "current", "Current", 1, false, fixture.Operation));

        // A key collision is a conflict with existing state, not malformed input, so it carries the
        // 409 catalog code rather than the 400 every other create rejection uses (DR-WFA-007).
        Assert.Equal("RESOURCE_ALREADY_EXISTS", exception.ErrorCode);
        Assert.Contains("current", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Db.WorkflowStates.CountAsync());
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_DependentIssueWithoutReplacement_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Issues.Add(fixture.CreateIssue(fixture.Current.Id));
        await fixture.Db.SaveChangesAsync();

        fixture.CreateState("todo", "Todo");
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.ArchiveWorkflowStateAsync(
                fixture.WorkspaceId, fixture.Current.Id, null, fixture.Operation));

        Assert.False((await fixture.Db.WorkflowStates.SingleAsync(s => s.Id == fixture.Current.Id)).IsArchived);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_DependentIssueWithActiveReplacement_ReassignsAndArchives()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var replacement = fixture.CreateState("todo", "Todo");
        var issue = fixture.CreateIssue(fixture.Current.Id);
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.ArchiveWorkflowStateAsync(
            fixture.WorkspaceId, fixture.Current.Id, replacement.Id, fixture.Operation);

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

        var service = CreateIssueService(fixture);
        var updated = await service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, todo.Id);

        Assert.Equal(IssueStatus.Todo, updated.Status);
        Assert.Equal(todo.Id, updated.WorkflowStateId);
        Assert.Equal(1, updated.Version);
        Assert.Null(updated.CompletedAt);
    }

    [Fact]
    public async Task ChangeStatusAsync_CustomState_UpdatesAuthoritativeStateAndKeepsLegacyProjection()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var qaReview = fixture.CreateState("qa_review", "QA review", order: 1);
        fixture.Db.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = fixture.WorkspaceId,
            FromStateId = backlog.Id,
            ToStateId = qaReview.Id,
        });
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Status = IssueStatus.Backlog;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);
        var updated = await service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, qaReview.Id);

        Assert.Equal(qaReview.Id, updated.WorkflowStateId);
        Assert.Equal(IssueStatus.Backlog, updated.Status);
        Assert.Equal(1, updated.Version);
        var activity = await fixture.Db.ActivityEvents.SingleAsync(e => e.IssueId == issue.Id);
        Assert.Contains("qa_review", activity.DataJson!);
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

        var service = CreateIssueService(fixture);

        var exception = await Assert.ThrowsAsync<WorkflowTransitionDeniedException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, done.Id));

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

        var service = CreateIssueService(fixture);
        var result = await service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, backlog.Id);

        Assert.Equal(0, result.Version);
        Assert.Equal(backlog.Id, result.WorkflowStateId);
    }

    [Fact]
    public async Task ChangeStatusAsync_TargetWorkflowStateMissing_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Status = IssueStatus.Backlog;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);

        var exception = await Assert.ThrowsAsync<WorkflowTransitionDeniedException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, WorkflowStateId.New()));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
        var unchanged = await fixture.Db.Issues.AsNoTracking().SingleAsync(i => i.Id == issue.Id);
        Assert.Equal(IssueStatus.Backlog, unchanged.Status);
        Assert.Equal(backlog.Id, unchanged.WorkflowStateId);
        Assert.Equal(0, unchanged.Version);
    }

    private static IssueService CreateIssueService(WorkflowFixture fixture) => new(
        fixture.Db,
        new FakePluginRegistry(),
        fixture.Engine,
        new NullRealtimeUpdatePublisher(),
        CorrelationContext.FromHeaderOrNew(null),
        NullLogger<IssueService>.Instance);

    private sealed class FakePluginRegistry : IPluginRegistry
    {
        public IReadOnlyList<IAnvilboardPlugin> All { get; } = [];
        public IReadOnlyList<IIngestionSource> IngestionSources { get; } = [];
        public IReadOnlyList<IWebhookReceiver> WebhookReceivers { get; } = [];
        public IReadOnlyList<IIssueHook> IssueHooks { get; } = [];
    }

}
