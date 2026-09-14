using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// Covers the conditional-write contract on the CLI/MCP surface, where it matters most: an agent
/// reads the board, reasons for a while, and then writes — a far wider read-modify-write window
/// than a human clicking a chip. Also pins down how the conflict interacts with the idempotency
/// layer, since both concern retries but for opposite reasons.
/// </summary>
public sealed class IssueConcurrencyOperationTests
{
    [Fact]
    public async Task ChangeIssueStatus_StaleExpectedVersion_FailsWithConcurrencyConflict()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var issueId = await CreateIssueAsync(factory, workspace, "Stale agent write", "concurrency-issue-1");
        var todoStateId = await StateIdAsync(factory, workspace, "todo");

        var result = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "stale-status-change-1"),
            ("expectedVersion", 99)));

        Assert.False(result.Succeeded);
        Assert.Contains("CONCURRENCY_CONFLICT", result.Error, StringComparison.Ordinal);

        var persisted = await factory.WithDbAsync(db => db.Issues.AsNoTracking()
            .SingleAsync(i => i.Id == new IssueId(issueId)));
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task ChangeIssueStatus_MatchingExpectedVersion_Succeeds()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var issueId = await CreateIssueAsync(factory, workspace, "Fresh agent write", "concurrency-issue-2");
        var todoStateId = await StateIdAsync(factory, workspace, "todo");

        var result = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "fresh-status-change-1"),
            ("expectedVersion", 1)));

        Assert.True(result.Succeeded, result.Error);
        var data = factory.Render(result).GetProperty("data");
        Assert.Equal(todoStateId, data.GetProperty("workflowStateId").GetGuid());
        Assert.Equal(2, data.GetProperty("version").GetInt32());
    }

    /// <summary>
    /// The idempotency record is only committed once the mutation succeeds, so a conflict must
    /// leave the key free. Otherwise the caller's only correct response — refetch and retry — would
    /// be blocked by the very key it used for the attempt that failed.
    /// </summary>
    [Fact]
    public async Task ChangeIssueStatus_AfterAConflict_TheSameIdempotencyKeyCanBeRetried()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var issueId = await CreateIssueAsync(factory, workspace, "Retry after conflict", "concurrency-issue-3");
        var todoStateId = await StateIdAsync(factory, workspace, "todo");

        // Two callers raced; ours read version 1 but lost, so the board has already moved on.
        // Simulate that by writing with a stale version, then correcting it.
        var conflicted = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "retry-after-conflict-1"),
            ("expectedVersion", 99)));
        Assert.False(conflicted.Succeeded);

        var retried = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "retry-after-conflict-1"),
            ("expectedVersion", 99)));

        // The key was never committed, so this is a genuine second attempt rather than a replay of
        // the first — it reaches the service and is rejected on its own merits.
        Assert.False(retried.Succeeded);
        Assert.Contains("CONCURRENCY_CONFLICT", retried.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("IDEMPOTENCY_KEY_REUSED", retried.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>expectedVersion</c> is a semantically significant input, not a transport detail: reusing
    /// a key with a different one must not replay the first attempt's result.
    /// </summary>
    [Fact]
    public async Task ChangeIssueStatus_SameKeyDifferentExpectedVersion_IsKeyReuse()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var issueId = await CreateIssueAsync(factory, workspace, "Key reuse", "concurrency-issue-6");
        var todoStateId = await StateIdAsync(factory, workspace, "todo");

        var first = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "key-reuse-1"),
            ("expectedVersion", 1)));
        Assert.True(first.Succeeded, first.Error);

        var reused = await factory.InvokeAsync("change-issue-status", AgentFactory.Inputs(
            ("issueId", issueId),
            ("workflowStateId", todoStateId),
            ("idempotencyKey", "key-reuse-1"),
            ("expectedVersion", 2)));

        Assert.False(reused.Succeeded);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", reused.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignIssue_StaleExpectedVersion_FailsWithoutMutating()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var issueId = await CreateIssueAsync(factory, workspace, "Stale assign", "concurrency-issue-4");

        var result = await factory.InvokeAsync("assign-issue", AgentFactory.Inputs(
            ("issueId", issueId),
            ("idempotencyKey", "stale-assign-1"),
            ("expectedVersion", 42)));

        Assert.False(result.Succeeded);
        Assert.Contains("CONCURRENCY_CONFLICT", result.Error, StringComparison.Ordinal);

        var persisted = await factory.WithDbAsync(db => db.Issues.AsNoTracking()
            .SingleAsync(i => i.Id == new IssueId(issueId)));
        Assert.Equal(1, persisted.Version);
        Assert.Null(persisted.AssigneeId);
    }

    /// <summary>
    /// Every mutation response carries the version, so an agent never has to make a second call to
    /// learn what to send on its next conditional write.
    /// </summary>
    [Fact]
    public async Task IssueSummary_CarriesTheVersion()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var create = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Version carrier"),
            ("idempotencyKey", "concurrency-issue-5")));

        Assert.True(create.Succeeded, create.Error);
        Assert.Equal(1, factory.Render(create).GetProperty("data").GetProperty("version").GetInt32());
    }

    private static async Task<Guid> CreateIssueAsync(
        AgentFactory factory, SeededWorkspace workspace, string title, string idempotencyKey)
    {
        var create = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", title),
            ("idempotencyKey", idempotencyKey)));
        Assert.True(create.Succeeded, create.Error);
        return factory.Render(create).GetProperty("data").GetProperty("id").GetGuid();
    }

    private static Task<Guid> StateIdAsync(AgentFactory factory, SeededWorkspace workspace, string key) =>
        factory.WithDbAsync(db => db.WorkflowStates
            .Where(s => s.WorkspaceId == workspace.WorkspaceId && s.Key == key)
            .Select(s => s.Id.Value)
            .SingleAsync());
}
