using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// End-to-end coverage for the five board-parity agent operations through the real catalog,
/// authorization policy, idempotency layer, application service, and SQLite schema. The agent
/// surface mirrors the REST routes one-for-one, so these prove an agent can reason about the same
/// board, history, and link vocabulary a human sees rather than a reduced subset.
/// </summary>
public sealed class BoardQueryOperationTests
{
    [Fact]
    public async Task QueryBoard_ReturnsGroupedIssuesAndEchoesAppliedQuery()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        await CreateIssueAsync(factory, workspace.TeamId.Value, "Board issue", "board-issue-1");

        var result = await factory.InvokeAsync("query-board");

        Assert.True(result.Succeeded, result.Error);
        var data = factory.Render(result).GetProperty("data");
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.NotEmpty(data.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task QueryBoard_SnakeCaseVocabulary_BindsToEnumMember()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        await CreateIssueAsync(factory, workspace.TeamId.Value, "Grouped issue", "grouped-issue-1");

        var result = await factory.InvokeAsync("query-board", AgentFactory.Inputs(
            ("groupBy", "assignee"),
            ("orderBy", "updated_at")));

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task QueryBoard_UnknownVocabularyToken_IsRejected()
    {
        // A typo in a closed vocabulary must fail rather than quietly answer a different question.
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var result = await factory.InvokeAsync("query-board", AgentFactory.Inputs(("groupBy", "not_a_grouping")));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task QueryBoard_DoesNotLeakAnotherWorkspacesIssues()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var foreign = await factory.BootstrapWorkspaceAsync("foreign-board");
        factory.UseApiToken(await factory.IssueApiTokenAsync(foreign.WorkspaceId, Role.Administrator));
        await CreateIssueAsync(factory, foreign.TeamId.Value, "Foreign issue", "foreign-board-issue-1");

        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var result = await factory.InvokeAsync("query-board");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, factory.Render(result).GetProperty("data").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task QueryBoard_WithoutReadBoardPermission_IsDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(
            workspace.WorkspaceId, Role.AutomationAgent, [Permission.ReadWriteComments]));

        var result = await factory.InvokeAsync("query-board");

        Assert.False(result.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", result.Error);
    }

    [Fact]
    public async Task ListIssueActivity_AfterMutation_ReturnsRenderedFeed()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var issueId = await CreateIssueAsync(factory, workspace.TeamId.Value, "Issue with history", "history-issue-1");

        var result = await factory.InvokeAsync("list-issue-activity", AgentFactory.Inputs(("issueId", issueId)));

        Assert.True(result.Succeeded, result.Error);
        var entries = factory.Render(result).GetProperty("data").GetProperty("entries").EnumerateArray().ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("text").GetString())));
    }

    [Fact]
    public async Task ListIssueActivity_ForeignIssue_IsDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var foreign = await factory.BootstrapWorkspaceAsync("foreign-activity");
        factory.UseApiToken(await factory.IssueApiTokenAsync(foreign.WorkspaceId, Role.Administrator));
        var foreignIssueId = await CreateIssueAsync(
            factory, foreign.TeamId.Value, "Foreign issue", "foreign-activity-issue-1");

        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var result = await factory.InvokeAsync("list-issue-activity", AgentFactory.Inputs(("issueId", foreignIssueId)));

        Assert.False(result.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", result.Error);
    }

    [Fact]
    public async Task ListIssueComments_ReturnsPersistedThread()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var issueId = await CreateIssueAsync(factory, workspace.TeamId.Value, "Commented issue", "commented-issue-1");

        var added = await factory.InvokeAsync("comment-on-issue", AgentFactory.Inputs(
            ("issueId", issueId),
            ("body", "Agent note"),
            ("idempotencyKey", "agent-comment-1")));
        Assert.True(added.Succeeded, added.Error);

        var result = await factory.InvokeAsync("list-issue-comments", AgentFactory.Inputs(("issueId", issueId)));

        Assert.True(result.Succeeded, result.Error);
        var comment = Assert.Single(factory.Render(result).GetProperty("data").EnumerateArray());
        Assert.Equal("Agent note", comment.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ListLinkTypes_ReturnsSuggestedVocabulary()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var result = await factory.InvokeAsync("list-link-types");

        Assert.True(result.Succeeded, result.Error);
        var types = factory.Render(result).GetProperty("data").EnumerateArray()
            .Select(element => element.GetString()).ToList();
        Assert.Contains("RELATED", types);
        Assert.Contains("BLOCKS", types);
    }

    [Fact]
    public async Task UpdateIssueLink_ChangesTypeAndIsReplaySafe()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var source = await CreateIssueAsync(factory, workspace.TeamId.Value, "Source", "link-source-1");
        var target = await CreateIssueAsync(factory, workspace.TeamId.Value, "Target", "link-target-1");

        var created = await factory.InvokeAsync("create-issue-link", AgentFactory.Inputs(
            ("issueId", source),
            ("targetIssueId", target),
            ("type", "RELATED"),
            ("idempotencyKey", "agent-link-1")));
        Assert.True(created.Succeeded, created.Error);
        var linkId = factory.Render(created).GetProperty("data").GetProperty("id").GetGuid();

        var inputs = AgentFactory.Inputs(
            ("issueId", source),
            ("linkId", linkId),
            ("type", "BLOCKS"),
            ("idempotencyKey", "agent-link-update-1"));
        var updated = await factory.InvokeAsync("update-issue-link", inputs);
        var replay = await factory.InvokeAsync("update-issue-link", inputs);

        Assert.True(updated.Succeeded, updated.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal("BLOCKS", factory.Render(updated).GetProperty("data").GetProperty("type").GetString());
        Assert.Equal(
            factory.Render(updated).GetProperty("data").GetProperty("id").GetGuid(),
            factory.Render(replay).GetProperty("data").GetProperty("id").GetGuid());
    }

    private static async Task<Guid> CreateIssueAsync(
        AgentFactory factory, Guid teamId, string title, string idempotencyKey)
    {
        var created = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", teamId),
            ("title", title),
            ("idempotencyKey", idempotencyKey)));
        Assert.True(created.Succeeded, created.Error);
        return factory.Render(created).GetProperty("data").GetProperty("id").GetGuid();
    }
}
