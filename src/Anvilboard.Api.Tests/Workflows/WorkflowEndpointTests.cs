using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Application.Workflows;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Api.Tests.Workflows;

/// <summary>
/// Exercises the complete workflow administration lifecycle through the production HTTP pipeline,
/// including the permission split between board-readable configuration and administrator writes.
/// </summary>
public sealed class WorkflowEndpointTests
{
    [Fact]
    public async Task ChangeIssueStatus_CustomWorkflowState_PersistsTheRequestedStateId()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var states = await client.GetFromJsonAsync<List<WorkflowStateDto>>("/api/workflow/states");
        var backlog = states!.Single(state => state.Key == "backlog");
        var customResponse = await client.PostAsJsonAsync("/api/workflow/states", new
        {
            key = "qa_review",
            displayName = "QA review",
            order = 10,
            isTerminal = false,
        });
        customResponse.EnsureSuccessStatusCode();
        var custom = await customResponse.Content.ReadFromJsonAsync<WorkflowStateDto>();
        var transitionResponse = await client.PostAsJsonAsync("/api/workflow/transitions", new
        {
            fromStateId = backlog.Id,
            toStateId = custom!.Id,
        });
        transitionResponse.EnsureSuccessStatusCode();

        var teamResponse = await client.PostAsJsonAsync("/api/teams", new
        {
            name = "Workflow team",
            key = "WF",
        });
        teamResponse.EnsureSuccessStatusCode();
        using var teamJson = JsonDocument.Parse(await teamResponse.Content.ReadAsStringAsync());
        var teamId = teamJson.RootElement.GetProperty("id").GetGuid();
        var issueResponse = await client.PostAsJsonAsync("/api/issues", new
        {
            teamId,
            title = "Custom workflow issue",
        });
        issueResponse.EnsureSuccessStatusCode();
        using var issueJson = JsonDocument.Parse(await issueResponse.Content.ReadAsStringAsync());
        var issueId = issueJson.RootElement.GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId}/status",
            new { workflowStateId = custom.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var updatedJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(custom.Id, updatedJson.RootElement.GetProperty("workflowStateId").GetGuid());
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        Assert.Equal(custom.Id, (await verifyDb.Issues.AsNoTracking().SingleAsync(i => i.Id == new IssueId(issueId))).WorkflowStateId.Value);
    }

    [Fact]
    public async Task ChangeIssueStatus_ForeignAndUnknownTargetStates_AreDeniedWithoutMutation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var teamResponse = await client.PostAsJsonAsync("/api/teams", new
        {
            name = "Scoped workflow team",
            key = "SCP",
        });
        teamResponse.EnsureSuccessStatusCode();
        using var teamJson = JsonDocument.Parse(await teamResponse.Content.ReadAsStringAsync());
        var issueResponse = await client.PostAsJsonAsync("/api/issues", new
        {
            teamId = teamJson.RootElement.GetProperty("id").GetGuid(),
            title = "Scoped transition issue",
        });
        issueResponse.EnsureSuccessStatusCode();
        using var issueJson = JsonDocument.Parse(await issueResponse.Content.ReadAsStringAsync());
        var issueId = new IssueId(issueJson.RootElement.GetProperty("id").GetGuid());

        var foreignWorkspaceId = await factory.SeedAdditionalWorkspaceAsync("foreign-workflow", "foreign-workflow-admin");
        var foreignState = new WorkflowState
        {
            Id = WorkflowStateId.New(),
            WorkspaceId = foreignWorkspaceId,
            Key = "foreign",
            DisplayName = "Foreign",
            Order = 0,
        };
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
            seedDb.WorkflowStates.Add(foreignState);
            await seedDb.SaveChangesAsync();
        }

        WorkflowStateId originalStateId;
        int originalVersion;
        int originalActivityCount;
        using (var beforeScope = factory.Services.CreateScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
            var before = await beforeDb.Issues.AsNoTracking().SingleAsync(i => i.Id == issueId);
            originalStateId = before.WorkflowStateId;
            originalVersion = before.Version;
            originalActivityCount = await beforeDb.ActivityEvents.CountAsync(e => e.IssueId == issueId);
        }

        var foreign = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId.Value}/status",
            new { workflowStateId = foreignState.Id.Value });
        var unknown = await client.PatchAsJsonAsync(
            $"/api/issues/{issueId.Value}/status",
            new { workflowStateId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(
            await foreign.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        var persisted = await verifyDb.Issues.AsNoTracking().SingleAsync(i => i.Id == issueId);
        Assert.Equal(IssueStatus.Backlog, persisted.Status);
        Assert.Equal(originalStateId, persisted.WorkflowStateId);
        Assert.Equal(originalVersion, persisted.Version);
        Assert.Equal(originalActivityCount, await verifyDb.ActivityEvents.CountAsync(e => e.IssueId == issueId));
    }

    [Fact]
    public async Task StateAndTransitionLifecycle_ReturnsTheDocumentedStatusCodes()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var statesResponse = await client.GetAsync("/api/workflow/states", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, statesResponse.StatusCode);
        var seededStates = await statesResponse.Content.ReadFromJsonAsync<List<WorkflowStateDto>>();
        var seeded = seededStates!.Single(state => state.Key == "backlog");

        var createState = await client.PostAsJsonAsync("/api/workflow/states", new
        {
            key = "qa_review",
            displayName = "QA review",
            order = 10,
            isTerminal = false,
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, createState.StatusCode);
        var createdState = await createState.Content.ReadFromJsonAsync<WorkflowStateDto>();
        Assert.NotNull(createdState);
        Assert.Equal($"/api/workflow/states/{createdState.Id}", createState.Headers.Location?.ToString());

        var updateState = await client.PatchAsJsonAsync($"/api/workflow/states/{createdState.Id}", new
        {
            displayName = "Quality review",
            order = 11,
            isTerminal = true,
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, updateState.StatusCode);
        var updatedState = await updateState.Content.ReadFromJsonAsync<WorkflowStateDto>();
        Assert.Equal("Quality review", updatedState!.DisplayName);
        Assert.True(updatedState.IsTerminal);

        var createTransition = await client.PostAsJsonAsync("/api/workflow/transitions", new
        {
            fromStateId = seeded.Id,
            toStateId = createdState.Id,
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, createTransition.StatusCode);
        var transition = await createTransition.Content.ReadFromJsonAsync<WorkflowTransitionDto>();
        Assert.NotNull(transition);

        var transitionsResponse = await client.GetAsync("/api/workflow/transitions", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, transitionsResponse.StatusCode);
        var transitions = await transitionsResponse.Content.ReadFromJsonAsync<List<WorkflowTransitionDto>>();
        Assert.Contains(transitions!, edge => edge.Id == transition.Id);

        var removeTransition = await client.DeleteAsync(
            $"/api/workflow/transitions/{transition.Id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, removeTransition.StatusCode);

        var archiveState = await client.DeleteAsync(
            $"/api/workflow/states/{createdState.Id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, archiveState.StatusCode);

        var activeResponse = await client.GetFromJsonAsync<List<WorkflowStateDto>>(
            "/api/workflow/states", CancellationToken.None);
        Assert.DoesNotContain(activeResponse!, state => state.Id == createdState.Id);

        var allResponse = await client.GetFromJsonAsync<List<WorkflowStateDto>>(
            "/api/workflow/states?includeArchived=true", CancellationToken.None);
        Assert.Contains(allResponse!, state => state.Id == createdState.Id && state.IsArchived);
    }

    [Fact]
    public async Task CreateState_DuplicateKey_ReturnsConflictWithCatalogCode()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var response = await client.PostAsJsonAsync("/api/workflow/states", new
        {
            key = "backlog",
            displayName = "Duplicate",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RESOURCE_ALREADY_EXISTS", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task PatchState_WithKey_ReturnsValidationFailedAndLeavesKeyUnchanged()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "workflow-key-rejection");
        var state = (await client.GetFromJsonAsync<List<WorkflowStateDto>>(
            "/api/workflow/states", CancellationToken.None))!
            .Single(item => item.Key == "backlog");

        var response = await client.PatchAsJsonAsync($"/api/workflow/states/{state.Id}", new
        {
            key = "renamed",
            displayName = "Renamed",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("title").GetString());

        var after = (await client.GetFromJsonAsync<List<WorkflowStateDto>>(
            "/api/workflow/states", CancellationToken.None))!
            .Single(item => item.Id == state.Id);
        Assert.Equal(state.Key, after.Key);
        Assert.Equal(state.DisplayName, after.DisplayName);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        var audit = await verifyDb.AuditEvents.AsNoTracking()
            .SingleAsync(item => item.Action == "workflow.state.rejected"
                && item.CorrelationId == "workflow-key-rejection");
        Assert.Equal(AuditChannel.Rest, audit.Channel);
        Assert.StartsWith("errorCode=VALIDATION_FAILED;", audit.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateAndTransitionIds_FromAnotherWorkspace_AreDeniedWithoutMutation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        var foreignWorkspace = await factory.SeedAdditionalWorkspaceAsync("foreign", "foreign-admin");

        Guid foreignStateId;
        Guid foreignTransitionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
            var first = new WorkflowState
            {
                Id = WorkflowStateId.New(), WorkspaceId = foreignWorkspace,
                Key = "first", DisplayName = "First", Order = 0,
            };
            var second = new WorkflowState
            {
                Id = WorkflowStateId.New(), WorkspaceId = foreignWorkspace,
                Key = "second", DisplayName = "Second", Order = 1,
            };
            var edge = new WorkflowTransition
            {
                Id = WorkflowTransitionId.New(), WorkspaceId = foreignWorkspace,
                FromStateId = first.Id, ToStateId = second.Id,
            };
            db.WorkflowStates.AddRange(first, second);
            db.WorkflowTransitions.Add(edge);
            await db.SaveChangesAsync();
            foreignStateId = first.Id.Value;
            foreignTransitionId = edge.Id.Value;
        }

        var patch = await client.PatchAsJsonAsync($"/api/workflow/states/{foreignStateId}", new
        {
            displayName = "Compromised",
        }, CancellationToken.None);
        var archive = await client.DeleteAsync(
            $"/api/workflow/states/{foreignStateId}", CancellationToken.None);
        var remove = await client.DeleteAsync(
            $"/api/workflow/transitions/{foreignTransitionId}", CancellationToken.None);

        await AssertWorkspaceDeniedAsync(patch);
        await AssertWorkspaceDeniedAsync(archive);
        await AssertWorkspaceDeniedAsync(remove);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        var state = await verifyDb.WorkflowStates.AsNoTracking()
            .SingleAsync(s => s.Id == new WorkflowStateId(foreignStateId));
        Assert.Equal("First", state.DisplayName);
        Assert.False(state.IsArchived);
        Assert.True(await verifyDb.WorkflowTransitions
            .AnyAsync(t => t.Id == new WorkflowTransitionId(foreignTransitionId)));
    }

    [Fact]
    public async Task WorkflowRoutes_Unauthenticated_ReturnUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var read = await client.GetAsync("/api/workflow/states", CancellationToken.None);
        var write = await client.PostAsJsonAsync("/api/workflow/states", new
        {
            key = "todo",
            displayName = "Todo",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task WorkflowWrites_NonAdministrator_ReturnForbiddenWhileReadsRemainAvailable()
    {
        await using var factory = new ApiFactory();
        using var bootstrapClient = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(bootstrapClient);

        using var memberClient = factory.CreateClient();
        memberClient.DefaultRequestHeaders.Add(
            "Cookie",
            await factory.SeedMemberAndGetSessionCookieAsync(memberClient, "contributor", Role.Contributor));

        var read = await memberClient.GetAsync("/api/workflow/states", CancellationToken.None);
        var write = await memberClient.PostAsJsonAsync("/api/workflow/states", new
        {
            key = "todo",
            displayName = "Todo",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        using var problem = JsonDocument.Parse(await write.Content.ReadAsStringAsync());
        Assert.Equal("WORKSPACE_ACCESS_DENIED", problem.RootElement.GetProperty("title").GetString());
    }

    private static async Task AssertWorkspaceDeniedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("WORKSPACE_ACCESS_DENIED", problem.RootElement.GetProperty("title").GetString());
    }
}
