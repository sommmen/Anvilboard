using System.Net;
using System.Net.Http.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Domain;

namespace Anvilboard.Api.Tests.Integrations;

/// <summary>
/// Covers <c>GET /api/integrations/health</c> (<c>docs/plans/integration-sync-health.md</c> §9) —
/// the first enforcement site for <see cref="Permission.ReadIntegrationHealth"/>.
/// </summary>
public sealed class IntegrationHealthEndpointTests
{
    [Fact]
    public async Task GetHealth_AsAdministrator_ReportsNeverSyncedIntegrationsAsStale()
    {
        await using var factory = new ApiFactory();
        using var client = await AuthenticatedClientAsync(factory);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Enabled);

        var response = await client.GetAsync("/api/integrations/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(await ReadHealthAsync(response));

        // "Never synced" must read as stale, not as healthy-by-absence: an integration that has
        // produced nothing is exactly what a freshness question is asking about.
        Assert.Equal(SyncCondition.Stale, entry.Condition);
        Assert.Equal(IntegrationProvider.GitHub, entry.Provider);
        Assert.Null(entry.LastSuccessAt);
        Assert.Equal(0, entry.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task GetHealth_ReportsAPausedIntegrationAsPaused()
    {
        await using var factory = new ApiFactory();
        using var client = await AuthenticatedClientAsync(factory);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Paused);

        var response = await client.GetAsync("/api/integrations/health");

        Assert.Equal(SyncCondition.Paused, Assert.Single(await ReadHealthAsync(response)).Condition);
    }

    [Fact]
    public async Task GetHealth_ExcludesRemovedIntegrations()
    {
        await using var factory = new ApiFactory();
        using var client = await AuthenticatedClientAsync(factory);

        var workspaceId = await factory.GetWorkspaceIdAsync("test-workspace");
        await factory.SeedIntegrationAsync(workspaceId, IntegrationProvider.GitHub, IntegrationStatus.Removed);

        var response = await client.GetAsync("/api/integrations/health");

        Assert.Empty(await ReadHealthAsync(response));
    }

    [Fact]
    public async Task GetHealth_DoesNotLeakAnotherWorkspacesIntegrations()
    {
        // Workspace comes from the authenticated actor, never the request, so an administrator of
        // one tenant cannot enumerate another tenant's integration estate.
        await using var factory = new ApiFactory();
        using var client = await AuthenticatedClientAsync(factory);

        var otherWorkspaceId = await factory.SeedAdditionalWorkspaceAsync("workspace-b", "admin-b");
        await factory.SeedIntegrationAsync(otherWorkspaceId, IntegrationProvider.GitHub, IntegrationStatus.Enabled);

        var response = await client.GetAsync("/api/integrations/health");

        Assert.Empty(await ReadHealthAsync(response));
    }

    [Fact]
    public async Task GetHealth_Unauthenticated_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(client);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/integrations/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<IReadOnlyList<HealthResponse>> ReadHealthAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<List<HealthResponse>>() ?? [];

    /// <summary>
    /// Mirrors the wire shape rather than referencing the DTO, so a change that silently adds a
    /// credential-bearing field to the response cannot pass unnoticed. Enums other than
    /// <see cref="Role"/> and <see cref="Permission"/> keep System.Text.Json's numeric form here.
    /// </summary>
    private sealed record HealthResponse(
        Guid IntegrationId,
        IntegrationProvider Provider,
        string PluginKey,
        IntegrationStatus Status,
        SyncCondition Condition,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt,
        SyncErrorCategory? LastErrorCategory,
        int ConsecutiveFailureCount,
        DateTimeOffset? NextAttemptNotBefore);
}
