using Anvilboard.Application.Workflows;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Workflows;

/// <summary>
/// Covers the workflow-configuration surface added by
/// <c>docs/plans/workflow-admin-surface.md</c> §12: the validation rules, the derived facts each
/// audit event must carry, and the edge cases that would otherwise only surface in production.
/// </summary>
public sealed class WorkflowConfigurationTests
{
    [Fact]
    public async Task ListWorkflowStatesAsync_ExcludesArchivedUnlessRequested()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var retired = fixture.CreateState("retired", "Retired", order: 5);
        retired.IsArchived = true;
        await fixture.Db.SaveChangesAsync();

        var active = await fixture.Engine.ListWorkflowStatesAsync(fixture.WorkspaceId);
        var all = await fixture.Engine.ListWorkflowStatesAsync(fixture.WorkspaceId, includeArchived: true);

        Assert.Equal(["current"], active.Select(state => state.Key));
        Assert.Equal(["current", "retired"], all.Select(state => state.Key));
    }

    [Fact]
    public async Task ListWorkflowStatesAsync_OrdersByBoardOrderAndProjectsSymbolicKey()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.CreateState("zeta", "Zeta", order: -1);
        await fixture.Db.SaveChangesAsync();

        var states = await fixture.Engine.ListWorkflowStatesAsync(fixture.WorkspaceId);

        Assert.Equal(["zeta", "current"], states.Select(state => state.Key));
        Assert.Equal(["ZETA", "CURRENT"], states.Select(state => state.SymbolicKey));
    }

    [Fact]
    public async Task ListWorkflowStatesAsync_IsScopedToTheRequestedWorkspace()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var states = await fixture.Engine.ListWorkflowStatesAsync(WorkspaceId.New());

        Assert.Empty(states);
    }

    [Fact]
    public async Task CreateWorkflowStateAsync_ValidInput_PersistsAndAuditsDerivedFacts()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var state = await fixture.Engine.CreateWorkflowStateAsync(
            fixture.WorkspaceId, " qa_review ", " QA review ", 3, false, fixture.Operation);

        Assert.Equal("qa_review", state.Key);
        Assert.Equal("QA review", state.DisplayName);
        Assert.Equal("QA_REVIEW", state.SymbolicKey);
        Assert.False(state.IsArchived);

        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.state.created"));
        Assert.Equal("workflow_state", audit.TargetType);
        Assert.Equal(state.Id.ToString(), audit.TargetId);
        Assert.Equal(fixture.Operation.CorrelationId, audit.CorrelationId);
        Assert.Equal("key=qa_review;order=3;isTerminal=false", audit.ResultSummary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("QA Review")]
    [InlineData("qa-review")]
    public async Task CreateWorkflowStateAsync_MalformedKey_ThrowsValidationFailed(string key)
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowStateAsync(
                fixture.WorkspaceId, key, "Whatever", 1, false, fixture.Operation));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Equal(1, await fixture.Db.WorkflowStates.CountAsync());
    }

    [Fact]
    public async Task CreateWorkflowStateAsync_RejectedInput_StillEmitsAnAuditEvent()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowStateAsync(
                fixture.WorkspaceId, "current", "Current", 1, false, fixture.Operation));

        // A rejected attempt is exactly the kind of activity an administrator investigating a
        // configuration change needs to see, so DR-WFA-008 audits it alongside successes.
        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.state.rejected"));
        Assert.Equal("key:current", audit.TargetId);
        Assert.StartsWith("errorCode=RESOURCE_ALREADY_EXISTS;", audit.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkflowStateAsync_AppliesOnlySuppliedFields()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var state = await fixture.Engine.UpdateWorkflowStateAsync(
            fixture.WorkspaceId, fixture.Current.Id, "Renamed", null, true, fixture.Operation);

        Assert.Equal("Renamed", state.DisplayName);
        Assert.Equal("current", state.Key);
        Assert.Equal(0, state.Order);
        Assert.True(state.IsTerminal);

        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.state.updated"));
        Assert.Equal("key=current;displayName=Renamed;isTerminal=true", audit.ResultSummary);
    }

    [Fact]
    public async Task UpdateWorkflowStateAsync_AttemptedKeyChange_IsRejectedAndAudited()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.UpdateWorkflowStateAsync(
                fixture.WorkspaceId, fixture.Current.Id, null, null, null, fixture.Operation, key: "renamed"));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Equal("current", (await fixture.Db.WorkflowStates.SingleAsync()).Key);
        var rejection = Assert.Single(await fixture.AuditEventsAsync("workflow.state.rejected"));
        Assert.Equal(fixture.Current.Id.Value.ToString(), rejection.TargetId);
        Assert.StartsWith("errorCode=VALIDATION_FAILED;", rejection.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkflowStateAsync_NoEffectiveChange_DoesNotAudit()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var state = await fixture.Engine.UpdateWorkflowStateAsync(
            fixture.WorkspaceId, fixture.Current.Id, "Current", 0, false, fixture.Operation);

        Assert.Equal("Current", state.DisplayName);
        Assert.Empty(await fixture.AuditEventsAsync("workflow.state.updated"));
    }

    [Fact]
    public async Task UpdateWorkflowStateAsync_UnknownState_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.UpdateWorkflowStateAsync(
                fixture.WorkspaceId, WorkflowStateId.New(), "Renamed", null, null, fixture.Operation));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_LastActiveState_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        // Without this guard the archive succeeds and then every subsequent issue creation fails,
        // because IssueService has no active state left to assign (AC-WFA-007).
        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.ArchiveWorkflowStateAsync(
                fixture.WorkspaceId, fixture.Current.Id, null, fixture.Operation));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.False((await fixture.Db.WorkflowStates.SingleAsync()).IsArchived);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_AuditsTheReassignedIssueCount()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var replacement = fixture.CreateState("todo", "Todo");
        fixture.Db.Issues.Add(fixture.CreateIssue(fixture.Current.Id));
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.ArchiveWorkflowStateAsync(
            fixture.WorkspaceId, fixture.Current.Id, replacement.Id, fixture.Operation);

        // The reassignment count is only knowable inside the service — the fact that makes
        // DR-WFA-001 ("audit in the service, not the adapters") load-bearing rather than stylistic.
        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.state.archived"));
        Assert.Equal("key=current;replacementKey=todo;reassignedIssues=1", audit.ResultSummary);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_AlreadyArchived_IsSilentNoOp()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var retired = fixture.CreateState("retired", "Retired");
        retired.IsArchived = true;
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.ArchiveWorkflowStateAsync(
            fixture.WorkspaceId, retired.Id, null, fixture.Operation);

        Assert.Empty(await fixture.AuditEventsAsync("workflow.state.archived"));
        Assert.Empty(await fixture.AuditEventsAsync("workflow.state.rejected"));
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_ReplacementIsTheStateBeingArchived_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.CreateState("todo", "Todo");
        fixture.Db.Issues.Add(fixture.CreateIssue(fixture.Current.Id));
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.ArchiveWorkflowStateAsync(
                fixture.WorkspaceId, fixture.Current.Id, fixture.Current.Id, fixture.Operation));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task ArchiveWorkflowStateAsync_DoesNotRemoveTransitionsTouchingTheState()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var todo = fixture.CreateState("todo", "Todo");
        fixture.CreateTransition(fixture.Current.Id, todo.Id);
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.ArchiveWorkflowStateAsync(
            fixture.WorkspaceId, fixture.Current.Id, null, fixture.Operation);

        // Edges touching an archived state are already inert: ValidateTransitionAsync refuses them
        // on the archived flag. Deleting them would destroy configuration an un-archive would need
        // (DR-WFA-005).
        Assert.Equal(1, await fixture.Db.WorkflowTransitions.CountAsync());

        var denial = await fixture.Engine.ValidateTransitionAsync(
            fixture.WorkspaceId, fixture.Current.Id, todo.Id);
        Assert.False(denial.IsAllowed);
    }

    [Fact]
    public async Task CreateWorkflowTransitionAsync_ValidEdge_PersistsAndAuditsKeysNotIds()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var todo = fixture.CreateState("todo", "Todo");
        await fixture.Db.SaveChangesAsync();

        var transition = await fixture.Engine.CreateWorkflowTransitionAsync(
            fixture.WorkspaceId, fixture.Current.Id, todo.Id, fixture.Operation);

        Assert.Equal("current", transition.FromStateKey);
        Assert.Equal("todo", transition.ToStateKey);

        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.transition.created"));
        Assert.Equal("workflow_transition", audit.TargetType);
        Assert.Equal("from=current;to=todo", audit.ResultSummary);

        var allowed = await fixture.Engine.ValidateTransitionAsync(
            fixture.WorkspaceId, fixture.Current.Id, todo.Id);
        Assert.True(allowed.IsAllowed);
    }

    [Fact]
    public async Task CreateWorkflowTransitionAsync_SelfLoop_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowTransitionAsync(
                fixture.WorkspaceId, fixture.Current.Id, fixture.Current.Id, fixture.Operation));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Equal(0, await fixture.Db.WorkflowTransitions.CountAsync());
    }

    [Fact]
    public async Task CreateWorkflowTransitionAsync_ArchivedEndpoint_ThrowsValidationFailed()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var retired = fixture.CreateState("retired", "Retired");
        retired.IsArchived = true;
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowTransitionAsync(
                fixture.WorkspaceId, fixture.Current.Id, retired.Id, fixture.Operation));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task CreateWorkflowTransitionAsync_DuplicateEdge_ThrowsResourceAlreadyExists()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var todo = fixture.CreateState("todo", "Todo");
        fixture.CreateTransition(fixture.Current.Id, todo.Id);
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowTransitionAsync(
                fixture.WorkspaceId, fixture.Current.Id, todo.Id, fixture.Operation));

        Assert.Equal("RESOURCE_ALREADY_EXISTS", exception.ErrorCode);
        Assert.Equal(1, await fixture.Db.WorkflowTransitions.CountAsync());
    }

    [Fact]
    public async Task CreateWorkflowTransitionAsync_EndpointInAnotherWorkspace_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.CreateWorkflowTransitionAsync(
                fixture.WorkspaceId, fixture.Current.Id, WorkflowStateId.New(), fixture.Operation));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task RemoveWorkflowTransitionAsync_RemovesEdgeAndAuditsEndpointKeys()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var todo = fixture.CreateState("todo", "Todo");
        var transition = fixture.CreateTransition(fixture.Current.Id, todo.Id);
        await fixture.Db.SaveChangesAsync();

        await fixture.Engine.RemoveWorkflowTransitionAsync(
            fixture.WorkspaceId, transition.Id, fixture.Operation);

        Assert.Equal(0, await fixture.Db.WorkflowTransitions.CountAsync());

        var audit = Assert.Single(await fixture.AuditEventsAsync("workflow.transition.removed"));
        Assert.Equal("from=current;to=todo", audit.ResultSummary);

        var denied = await fixture.Engine.ValidateTransitionAsync(
            fixture.WorkspaceId, fixture.Current.Id, todo.Id);
        Assert.False(denied.IsAllowed);
    }

    [Fact]
    public async Task RemoveWorkflowTransitionAsync_UnknownTransition_ThrowsReferencedEntityNotFound()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Engine.RemoveWorkflowTransitionAsync(
                fixture.WorkspaceId, WorkflowTransitionId.New(), fixture.Operation));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task ListWorkflowTransitionsAsync_DenormalizesBothEndpointKeys()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var todo = fixture.CreateState("todo", "Todo");
        fixture.CreateTransition(fixture.Current.Id, todo.Id);
        await fixture.Db.SaveChangesAsync();

        var transitions = await fixture.Engine.ListWorkflowTransitionsAsync(fixture.WorkspaceId);

        var edge = Assert.Single(transitions);
        Assert.Equal(fixture.Current.Id.Value, edge.FromStateId);
        Assert.Equal("current", edge.FromStateKey);
        Assert.Equal(todo.Id.Value, edge.ToStateId);
        Assert.Equal("todo", edge.ToStateKey);
    }
}
