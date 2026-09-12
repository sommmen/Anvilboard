using System.Text.Json;
using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// End-to-end checks that the CLI/MCP surface authenticates, authorizes, and attributes writes
/// through the real DI graph and a real database (MAJ-015, MAJ-016, MAJ-001).
/// </summary>
public sealed class AgentAuthorizationIntegrationTests
{
    [Fact]
    public async Task Invocation_WithoutCredential_IsDeniedBeforeAnyWrite()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();

        // No token configured: this is the exact pre-fix scenario where any local process could
        // mutate the board.
        var result = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Should never exist"),
            ("idempotencyKey", "unauthenticated-1")));

        Assert.False(result.Succeeded);
        Assert.Contains("AUTHENTICATION_REQUIRED", result.Error);

        await factory.WithDbAsync(async db =>
            Assert.Empty(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task Invocation_WithUnknownToken_IsDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken("tok-not-in-the-database");

        var result = await factory.InvokeAsync("list-issues");

        Assert.False(result.Succeeded);
        Assert.Contains("CREDENTIAL_INVALID_OR_EXPIRED", result.Error);
    }

    [Fact]
    public async Task Invocation_WithExpiredToken_IsDenied()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(
            workspace.WorkspaceId,
            Role.Administrator,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("list-issues");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Invocation_WithoutTheRequiredPermission_IsDeniedBeforeAnyWrite()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();

        // A read-only token can list the board but must not be able to create issues.
        var token = await factory.IssueApiTokenAsync(
            workspace.WorkspaceId,
            Role.AutomationAgent,
            grantedPermissions: [Permission.ReadBoard, Permission.ReadDashboard]);
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "A read-only token should not create this"),
            ("idempotencyKey", "read-only-1")));

        Assert.False(result.Succeeded);
        await factory.WithDbAsync(async db =>
            Assert.Empty(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task AutomationAgentToken_CannotReachBackupOperations()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();

        // AutomationAgent deliberately lacks ManageBackupRestore: a leaked automation token must
        // not be able to exfiltrate the whole workspace as a backup archive.
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("create-backup");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SuccessfulMutation_AttributesTheWriteToTheAuthenticatedMember()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Forge the anvil"),
            ("idempotencyKey", "attribution-1")));

        Assert.True(result.Succeeded, result.Error);

        await factory.WithDbAsync(async db =>
        {
            var issue = await db.Issues.AsNoTracking().SingleAsync();
            var tokenMemberId = await db.ApiTokens.AsNoTracking()
                .Select(t => t.MemberId)
                .SingleAsync();

            // Pre-fix this was a hard-coded synthetic "automation" id; it must now be the real
            // member behind the presented token.
            Assert.Equal(tokenMemberId, issue.CreatedById);
            Assert.Equal(workspace.TeamId, issue.TeamId);
        });
    }

    [Fact]
    public async Task SuccessfulInvocation_ReturnsTheVersionedEnvelope()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("list-issues");

        Assert.True(result.Succeeded, result.Error);
        var payload = factory.Render(result);
        Assert.Equal("1.0", payload.GetProperty("apiVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("correlationId").GetString()));
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ReplayingAnIdempotencyKey_ReturnsTheOriginalResultWithoutASecondWrite()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var inputs = AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Retried under a flaky connection"),
            ("idempotencyKey", "replay-1"));

        var first = await factory.InvokeAsync("create-issue", inputs);
        var second = await factory.InvokeAsync("create-issue", inputs);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);

        var firstId = factory.Render(first)
            .GetProperty("data").GetProperty("id").GetString();
        var secondId = factory.Render(second)
            .GetProperty("data").GetProperty("id").GetString();
        Assert.Equal(firstId, secondId);

        await factory.WithDbAsync(async db =>
            Assert.Single(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task ReusingAnIdempotencyKeyWithDifferentInputs_IsRejected()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var first = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Original title"),
            ("idempotencyKey", "collision-1")));
        Assert.True(first.Succeeded, first.Error);

        var second = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "Completely different title"),
            ("idempotencyKey", "collision-1")));

        Assert.False(second.Succeeded);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", second.Error);

        await factory.WithDbAsync(async db =>
            Assert.Single(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task IdempotencyKeys_AreScopedToTheAuthenticatedWorkspace()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var first = await factory.BootstrapWorkspaceAsync("workspace-one");

        var firstToken = await factory.IssueApiTokenAsync(first.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(firstToken);
        var firstResult = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", first.TeamId.Value),
            ("title", "Workspace one issue"),
            ("idempotencyKey", "shared-key")));
        Assert.True(firstResult.Succeeded, firstResult.Error);

        // The same key in a different workspace is a different request, not a replay.
        var second = await factory.BootstrapWorkspaceAsync("workspace-two");
        var secondToken = await factory.IssueApiTokenAsync(second.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(secondToken);
        var secondResult = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", second.TeamId.Value),
            ("title", "Workspace two issue"),
            ("idempotencyKey", "shared-key")));

        Assert.True(secondResult.Succeeded, secondResult.Error);
        await factory.WithDbAsync(async db =>
            Assert.Equal(2, await db.Issues.AsNoTracking().CountAsync()));
    }

    [Fact]
    public async Task MissingIdempotencyKey_IsRejectedAsAValidationFailure()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        var token = await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        var result = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", workspace.TeamId.Value),
            ("title", "No key supplied"),
            ("idempotencyKey", "   ")));

        Assert.False(result.Succeeded);
        Assert.Contains("VALIDATION_FAILED", result.Error);
        await factory.WithDbAsync(async db =>
            Assert.Empty(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task ListIssues_WithoutTeamFilter_ReturnsOnlyAuthenticatedWorkspace()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var first = await factory.BootstrapWorkspaceAsync("workspace-one");
        var second = await factory.BootstrapWorkspaceAsync("workspace-two");
        var token = await factory.IssueApiTokenAsync(first.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(token);

        await factory.WithDbAsync(async db =>
        {
            var stateByWorkspace = await db.WorkflowStates
                .Where(state => state.Key == "backlog")
                .ToDictionaryAsync(state => state.WorkspaceId, state => state.Id);
            db.Issues.AddRange(
                new Issue
                {
                    Id = IssueId.New(),
                    Key = "ONE-1",
                    TeamId = first.TeamId,
                    WorkflowStateId = stateByWorkspace[first.WorkspaceId],
                    Title = "Visible issue",
                    Status = IssueStatus.Backlog,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new Issue
                {
                    Id = IssueId.New(),
                    Key = "TWO-1",
                    TeamId = second.TeamId,
                    WorkflowStateId = stateByWorkspace[second.WorkspaceId],
                    Title = "Foreign issue",
                    Status = IssueStatus.Backlog,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        });

        var result = await factory.InvokeAsync("list-issues");

        Assert.True(result.Succeeded, result.Error);
        var response = factory.Render(result);
        var issues = response.GetProperty("data").EnumerateArray().ToList();
        var issue = Assert.Single(issues);
        Assert.Equal("Visible issue", issue.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DashboardSummary_WithoutTeamFilter_AggregatesOnlyAuthenticatedWorkspace()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var first = await factory.BootstrapWorkspaceAsync("workspace-one");
        var second = await factory.BootstrapWorkspaceAsync("workspace-two");
        var token = await factory.IssueApiTokenAsync(
            first.WorkspaceId,
            Role.AutomationAgent,
            [Permission.ReadDashboard]);
        factory.UseApiToken(token);

        await factory.WithDbAsync(async db =>
        {
            var stateByWorkspace = await db.WorkflowStates
                .Where(state => state.Key == "backlog")
                .ToDictionaryAsync(state => state.WorkspaceId, state => state.Id);
            db.Issues.AddRange(
                new Issue
                {
                    Id = IssueId.New(),
                    Key = "ONE-1",
                    TeamId = first.TeamId,
                    WorkflowStateId = stateByWorkspace[first.WorkspaceId],
                    Title = "Visible issue",
                    Status = IssueStatus.Backlog,
                    Source = IntegrationProvider.Local,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new Issue
                {
                    Id = IssueId.New(),
                    Key = "TWO-1",
                    TeamId = second.TeamId,
                    WorkflowStateId = stateByWorkspace[second.WorkspaceId],
                    Title = "Foreign issue",
                    Status = IssueStatus.Backlog,
                    Source = IntegrationProvider.Local,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        });

        var result = await factory.InvokeAsync("dashboard-summary");

        Assert.True(result.Succeeded, result.Error);
        var response = factory.Render(result);
        var data = response.GetProperty("data");
        Assert.Equal(1, data.GetProperty("issuesByStatus").GetProperty("Backlog").GetInt32());
        Assert.Equal(1, data.GetProperty("createdLast7Days").GetInt32());
    }

    [Fact]
    public async Task Invocation_CannotReachIssuesInAnotherWorkspace()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var first = await factory.BootstrapWorkspaceAsync("workspace-one");
        var second = await factory.BootstrapWorkspaceAsync("workspace-two");

        var firstToken = await factory.IssueApiTokenAsync(first.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(firstToken);

        // teamId belongs to the *other* workspace; the write must not land.
        var result = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", second.TeamId.Value),
            ("title", "Cross-workspace write"),
            ("idempotencyKey", "cross-1")));

        Assert.False(result.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", result.Error);

        await factory.WithDbAsync(async db =>
            Assert.Empty(await db.Issues.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task Invocation_CannotAssignMembersFromAnotherWorkspace()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var first = await factory.BootstrapWorkspaceAsync("workspace-one");
        var second = await factory.BootstrapWorkspaceAsync("workspace-two");

        var firstToken = await factory.IssueApiTokenAsync(first.WorkspaceId, Role.AutomationAgent);
        factory.UseApiToken(firstToken);

        var created = await factory.InvokeAsync("create-issue", AgentFactory.Inputs(
            ("teamId", first.TeamId.Value),
            ("title", "Local issue"),
            ("idempotencyKey", "local-1")));
        Assert.True(created.Succeeded);

        var issueId = await factory.WithDbAsync(async db =>
            (await db.Issues.AsNoTracking().SingleAsync()).Id.Value);

        // assigneeId belongs to the *other* workspace.
        var result = await factory.InvokeAsync("assign-issue", AgentFactory.Inputs(
            ("issueId", issueId),
            ("assigneeId", second.AdministratorId.Value),
            ("idempotencyKey", "assign-1")));

        Assert.False(result.Succeeded);
        Assert.Contains("WORKSPACE_ACCESS_DENIED", result.Error);

        await factory.WithDbAsync(async db =>
            Assert.Null((await db.Issues.AsNoTracking().SingleAsync()).AssigneeId));
    }
}
