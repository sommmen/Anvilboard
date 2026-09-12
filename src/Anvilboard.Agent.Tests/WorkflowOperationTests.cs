using System.Text.Json;
using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// End-to-end coverage for the seven CLI/MCP workflow operations through the real catalog,
/// authorization policy, idempotency layer, application service, and SQLite schema.
/// </summary>
public sealed class WorkflowOperationTests
{
    [Fact]
    public async Task WorkflowOperations_CompleteTheConfigurationLifecycle()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator);
        factory.UseApiToken(token);

        var initial = await factory.InvokeAsync("list-workflow-states");
        Assert.True(initial.Succeeded, initial.Error);
        var initialData = factory.Render(initial).GetProperty("data");
        Assert.Equal(6, initialData.GetArrayLength());
        var backlogId = initialData.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "backlog")
            .GetProperty("id").GetGuid();

        var createState = await factory.InvokeAsync("create-workflow-state", AgentFactory.Inputs(
            ("key", "qa_review"),
            ("displayName", "QA review"),
            ("order", 3),
            ("isTerminal", false),
            ("idempotencyKey", "workflow-state-create-1")));
        Assert.True(createState.Succeeded, createState.Error);
        var stateId = factory.Render(createState).GetProperty("data").GetProperty("id").GetGuid();

        var updateState = await factory.InvokeAsync("update-workflow-state", AgentFactory.Inputs(
            ("stateId", stateId),
            ("displayName", "Quality review"),
            ("order", 4),
            ("isTerminal", true),
            ("idempotencyKey", "workflow-state-update-1")));
        Assert.True(updateState.Succeeded, updateState.Error);
        Assert.Equal("Quality review", factory.Render(updateState).GetProperty("data").GetProperty("displayName").GetString());

        var createEdge = await factory.InvokeAsync("create-workflow-transition", AgentFactory.Inputs(
            ("fromStateId", backlogId),
            ("toStateId", stateId),
            ("idempotencyKey", "workflow-transition-create-1")));
        Assert.True(createEdge.Succeeded, createEdge.Error);
        var transitionId = factory.Render(createEdge).GetProperty("data").GetProperty("id").GetGuid();

        var createIssue = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Custom workflow issue"),
            ("idempotencyKey", "workflow-issue-create-1")));
        Assert.True(createIssue.Succeeded, createIssue.Error);
        var issueId = factory.Render(createIssue).GetProperty("data").GetProperty("id").GetGuid();
        var changeStatusInputs = AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", stateId),
            ("idempotencyKey", "workflow-status-change-1"));
        var changeStatus = await factory.InvokeAsync("change-issue-status", changeStatusInputs);
        var replay = await factory.InvokeAsync("change-issue-status", changeStatusInputs);
        Assert.True(changeStatus.Succeeded, changeStatus.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(stateId, factory.Render(changeStatus).GetProperty("data").GetProperty("workflowStateId").GetGuid());
        Assert.Equal(stateId, factory.Render(replay).GetProperty("data").GetProperty("workflowStateId").GetGuid());

        var listedEdges = await factory.InvokeAsync("list-workflow-transitions");
        Assert.True(listedEdges.Succeeded, listedEdges.Error);
        Assert.Contains(
            factory.Render(listedEdges).GetProperty("data").EnumerateArray(),
            edge => edge.GetProperty("id").GetGuid() == transitionId);

        var removeEdge = await factory.InvokeAsync("remove-workflow-transition", AgentFactory.Inputs(
            ("transitionId", transitionId),
            ("idempotencyKey", "workflow-transition-remove-1")));
        Assert.True(removeEdge.Succeeded, removeEdge.Error);

        var archiveState = await factory.InvokeAsync("archive-workflow-state", AgentFactory.Inputs(
            ("stateId", stateId),
            ("replacementStateId", backlogId),
            ("idempotencyKey", "workflow-state-archive-1")));
        Assert.True(archiveState.Succeeded, archiveState.Error);

        var active = await factory.InvokeAsync("list-workflow-states");
        Assert.True(active.Succeeded, active.Error);
        Assert.DoesNotContain(
            factory.Render(active).GetProperty("data").EnumerateArray(),
            state => state.GetProperty("id").GetGuid() == stateId);

        var all = await factory.InvokeAsync("list-workflow-states", AgentFactory.Inputs(("includeArchived", true)));
        Assert.True(all.Succeeded, all.Error);
        Assert.Contains(
            factory.Render(all).GetProperty("data").EnumerateArray(),
            state => state.GetProperty("id").GetGuid() == stateId && state.GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task Mutation_ReplayedWithSameIdempotencyKey_ReturnsOriginalResultAndWritesOnce()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator);
        factory.UseApiToken(token);
        var inputs = AgentFactory.Inputs(
            ("key", "qa_review"),
            ("displayName", "QA review"),
            ("order", 3),
            ("isTerminal", false),
            ("idempotencyKey", "workflow-state-replay-1"));

        var first = await factory.InvokeAsync("create-workflow-state", inputs);
        var replay = await factory.InvokeAsync("create-workflow-state", inputs);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(
            factory.Render(first).GetProperty("data").GetProperty("id").GetGuid(),
            factory.Render(replay).GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal(1, await factory.WithDbAsync(db => db.WorkflowStates.CountAsync(s => s.Key == "qa_review")));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "workflow.state.created")));
    }

    [Fact]
    public async Task ChangeIssueStatus_ForeignAndUnknownTargetStates_AreDeniedWithoutMutation()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var foreignWorkspace = await factory.BootstrapWorkspaceAsync("foreign-workflow");
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator);
        factory.UseApiToken(token);

        var createIssue = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Scoped transition issue"),
            ("idempotencyKey", "scoped-transition-issue-1")));
        Assert.True(createIssue.Succeeded, createIssue.Error);
        var issueId = factory.Render(createIssue).GetProperty("data").GetProperty("id").GetGuid();
        var foreignStateId = await factory.WithDbAsync(db => db.WorkflowStates
            .Where(s => s.WorkspaceId == foreignWorkspace.WorkspaceId)
            .Select(s => s.Id.Value)
            .FirstAsync());

        var before = await factory.WithDbAsync(db => db.Issues.AsNoTracking()
            .SingleAsync(i => i.Id == new IssueId(issueId)));
        var activityCount = await factory.WithDbAsync(db => db.ActivityEvents.CountAsync(e => e.IssueId == before.Id));
        var foreign = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", foreignStateId),
            ("idempotencyKey", "foreign-state-change-1")));
        var unknown = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", Guid.NewGuid()),
            ("idempotencyKey", "unknown-state-change-1")));

        Assert.False(foreign.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", foreign.Error);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", unknown.Error);
        var persisted = await factory.WithDbAsync(db => db.Issues.AsNoTracking()
            .SingleAsync(i => i.Id == before.Id));
        Assert.Equal(before.WorkflowStateId, persisted.WorkflowStateId);
        Assert.Equal(before.Version, persisted.Version);
        Assert.Equal(activityCount, await factory.WithDbAsync(db => db.ActivityEvents.CountAsync(e => e.IssueId == before.Id)));
    }

    [Fact]
    public async Task WorkflowMutation_WithoutManagePermission_IsDeniedBeforeWrite()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(
            workspace.WorkspaceId,
            Role.AutomationAgent,
            [Permission.ReadBoard]);
        factory.UseApiToken(token);

        var read = await factory.InvokeAsync("list-workflow-states");
        var write = await factory.InvokeAsync("create-workflow-state", AgentFactory.Inputs(
            ("key", "forbidden"),
            ("displayName", "Forbidden"),
            ("order", 3),
            ("isTerminal", false),
            ("idempotencyKey", "workflow-state-forbidden-1")));

        Assert.True(read.Succeeded, read.Error);
        Assert.False(write.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", write.Error);
        Assert.Equal(0, await factory.WithDbAsync(db => db.WorkflowStates.CountAsync(s => s.Key == "forbidden")));
    }

    [Fact]
    public async Task WorkflowOperations_WithoutCredential_AreDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        await factory.BootstrapWorkspaceAsync();

        var result = await factory.InvokeAsync("list-workflow-states");

        Assert.False(result.Succeeded);
        Assert.Contains("AUTHENTICATION_REQUIRED", result.Error);
    }
}
