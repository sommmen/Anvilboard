using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anvilboard.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application-layer services (<see cref="IssueService"/>,
    /// <see cref="DashboardService"/>). Called from both the ASP.NET Core API host and the CLI/MCP
    /// agent host so the two surfaces share identical business logic — a host only differs in how
    /// it exposes these services (HTTP endpoints vs. <c>[AgentOperation]</c>-annotated CLI/MCP
    /// commands).
    /// </summary>
    public static IServiceCollection AddAnvilboardApplication(this IServiceCollection services)
    {
        services.AddScoped<IssueService>();
        services.AddScoped<DashboardService>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="SyncCoordinator"/> background service that polls ingestion
    /// plugins. Split out from <see cref="AddAnvilboardApplication"/> so a one-shot CLI invocation
    /// (the <c>Anvilboard.Agent</c> host in CLI mode) can use the same services without also
    /// starting a long-running background loop; the API host and the MCP server (which stays
    /// resident) both call this in addition.
    /// </summary>
    public static IServiceCollection AddAnvilboardSyncCoordinator(this IServiceCollection services)
    {
        services.AddHostedService<SyncCoordinator>();
        return services;
    }
}
