using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// End-to-end coverage for <c>list-audit-events</c> through the real catalog, authorization policy,
/// application service, and SQLite schema. The audit trail is the surface an agent reads before
/// proposing a change ("did a human already do this?"), so these prove the trail an agent sees is
/// the one actually recorded — filtered, bounded, and scoped to its own workspace.
/// </summary>
public sealed class AuditOperationTests
{
    [Fact]
    public async Task ListAuditEvents_ReturnsRecordedHistory()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:alice", AuditChannel.Cli, "workflow.state.created");

        var result = await factory.InvokeAsync("list-audit-events");

        Assert.True(result.Succeeded, result.Error);
        var data = factory.Render(result).GetProperty("data");
        var events = data.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "workflow.state.created");
        Assert.False(data.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// A real mutation through the agent surface must land in the trail the same operation reads, or
    /// the surface would only ever be able to see synthetically seeded rows.
    /// </summary>
    [Fact]
    public async Task ListAuditEvents_SeesEventsWrittenByAnotherAgentOperation()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var created = await factory.InvokeAsync("create-workflow-state", AgentFactory.Inputs(
            ("key", "qa_review"),
            ("displayName", "QA review"),
            ("order", 3),
            ("idempotencyKey", "audit-workflow-state-1")));
        Assert.True(created.Succeeded, created.Error);

        var result = await factory.InvokeAsync("list-audit-events");

        Assert.True(result.Succeeded, result.Error);
        var events = factory.Render(result).GetProperty("data").GetProperty("events").EnumerateArray().ToList();
        var recorded = Assert.Single(events, e => e.GetProperty("action").GetString() == "workflow.state.created");
        Assert.Equal("CLI", recorded.GetProperty("channel").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task ListAuditEvents_FilteredByActor_NarrowsResults()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:alice", AuditChannel.Cli, "issue.created");
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:bob", AuditChannel.Rest, "issue.created");

        var result = await factory.InvokeAsync("list-audit-events", AgentFactory.Inputs(("actorId", "member:alice")));

        Assert.True(result.Succeeded, result.Error);
        var events = factory.Render(result).GetProperty("data").GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("member:alice", e.GetProperty("actorId").GetString()));
    }

    [Fact]
    public async Task ListAuditEvents_FilteredByChannel_AcceptsVocabularyToken()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:alice", AuditChannel.Cli, "issue.created");
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:bob", AuditChannel.Rest, "issue.created");

        var result = await factory.InvokeAsync("list-audit-events", AgentFactory.Inputs(("channel", "rest")));

        Assert.True(result.Succeeded, result.Error);
        var events = factory.Render(result).GetProperty("data").GetProperty("events").EnumerateArray().ToList();
        var only = Assert.Single(events);
        Assert.Equal("member:bob", only.GetProperty("actorId").GetString());
    }

    [Fact]
    public async Task ListAuditEvents_UnrecognizedChannel_IsRejectedRatherThanIgnored()
    {
        // A typo must not quietly answer a different, broader question than the caller asked.
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var result = await factory.InvokeAsync("list-audit-events", AgentFactory.Inputs(("channel", "carrier-pigeon")));

        Assert.False(result.Succeeded);
        Assert.Contains("VALIDATION_FAILED", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task ListAuditEvents_LimitOutsideAllowedRange_Fails(int limit)
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        var result = await factory.InvokeAsync("list-audit-events", AgentFactory.Inputs(("limit", limit)));

        Assert.False(result.Succeeded);
        Assert.Contains("Limit must be between 1 and 200", result.Error);
    }

    [Fact]
    public async Task ListAuditEvents_WithoutReadAuditPermission_IsDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();

        // Role.AutomationAgent deliberately lacks ReadAudit: a token that can manage issues must not
        // thereby be able to read who else touched the workspace.
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent));
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:alice", AuditChannel.Cli, "issue.created");

        var result = await factory.InvokeAsync("list-audit-events");

        Assert.False(result.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", result.Error);
    }

    [Fact]
    public async Task ListAuditEvents_NeverReturnsAnotherWorkspacesHistory()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var foreign = await factory.BootstrapWorkspaceAsync("foreign-audit");
        await SeedAuditEventAsync(factory, foreign.WorkspaceId, "member:intruder", AuditChannel.Rest, "issue.deleted");
        await SeedAuditEventAsync(factory, workspace.WorkspaceId, "member:alice", AuditChannel.Cli, "issue.created");

        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));
        var result = await factory.InvokeAsync("list-audit-events");

        Assert.True(result.Succeeded, result.Error);
        var events = factory.Render(result).GetProperty("data").GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, e => e.GetProperty("actorId").GetString() == "member:intruder");
    }

    private static Task SeedAuditEventAsync(
        AgentFactory factory,
        WorkspaceId workspaceId,
        string actorId,
        AuditChannel channel,
        string action) =>
        factory.WithDbAsync(async (AnvilboardDbContext db) =>
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = AuditEventId.New(),
                WorkspaceId = workspaceId,
                ActorId = actorId,
                Channel = channel,
                Action = action,
                TargetType = "issue",
                TargetId = Guid.NewGuid().ToString(),
                CorrelationId = Guid.NewGuid().ToString(),
                OccurredAt = DateTimeOffset.UtcNow,
                ResultSummary = "seeded",
            });

            await db.SaveChangesAsync();
        });
}
