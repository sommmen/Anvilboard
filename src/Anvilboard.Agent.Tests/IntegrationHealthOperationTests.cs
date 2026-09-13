using Anvilboard.Agent.Tests.Testing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// End-to-end coverage for the <c>list-integration-health</c> agent operation
/// (<c>docs/plans/integration-sync-health.md</c> §9) through the real catalog, authorization
/// policy, application service, and SQLite schema.
/// </summary>
public sealed class IntegrationHealthOperationTests
{
    [Fact]
    public async Task ListIntegrationHealth_ReportsANeverSyncedIntegrationAsStale()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        await SeedIntegrationAsync(factory, workspace.WorkspaceId, IntegrationStatus.Enabled);

        var result = await factory.InvokeAsync("list-integration-health");

        Assert.True(result.Succeeded, result.Error);
        var entry = Assert.Single(factory.Render(result).GetProperty("data").EnumerateArray());
        Assert.Equal(nameof(SyncCondition.Stale), entry.GetProperty("condition").GetString());
        Assert.Equal(nameof(IntegrationProvider.GitHub), entry.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task ListIntegrationHealth_ReportsAPausedIntegrationAsPaused()
    {
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Administrator));

        await SeedIntegrationAsync(factory, workspace.WorkspaceId, IntegrationStatus.Paused);

        var result = await factory.InvokeAsync("list-integration-health");

        Assert.True(result.Succeeded, result.Error);
        var entry = Assert.Single(factory.Render(result).GetProperty("data").EnumerateArray());
        Assert.Equal(nameof(SyncCondition.Paused), entry.GetProperty("condition").GetString());
    }

    [Fact]
    public async Task ListIntegrationHealth_WithoutTheReadPermission_IsRefused()
    {
        // A contributor works the board but must not enumerate the integration estate behind it;
        // only Administrator and Coordinator hold ReadIntegrationHealth.
        await using var factory = new AgentFactory();
        await factory.InitializeAsync();
        var workspace = await factory.BootstrapWorkspaceAsync();
        factory.UseApiToken(await factory.IssueApiTokenAsync(workspace.WorkspaceId, Role.Contributor));

        await SeedIntegrationAsync(factory, workspace.WorkspaceId, IntegrationStatus.Enabled);

        var result = await factory.InvokeAsync("list-integration-health");

        Assert.False(result.Succeeded);
    }

    private static Task SeedIntegrationAsync(
        AgentFactory factory,
        WorkspaceId workspaceId,
        IntegrationStatus status) =>
        factory.WithDbAsync(async db =>
        {
            db.Integrations.Add(new Integration
            {
                Id = IntegrationId.New(),
                WorkspaceId = workspaceId,
                Provider = IntegrationProvider.GitHub,
                SettingsJson = "{}",
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
}
