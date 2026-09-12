using Anvilboard.Application.Artifacts;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Authorization;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Backup;
using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Integrations;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Realtime;
using Anvilboard.Application.Sync;
using Anvilboard.Application.Workflows;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        services.AddScoped<IBoardQueryService, BoardQueryService>();
        services.AddScoped<IssueLinkService>();
        services.AddScoped<IArtifactService, ArtifactService>();
        services.AddScoped<IIntegrationService, IntegrationService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<IWorkflowService, WorkflowEngine>();
        services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
        services.TryAddScoped<IAuditService, AuditService>();
        services.AddScoped<IIdempotencyService, IdempotencyService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddSingleton<IRestoreCoordinator, RestoreCoordinator>();

        // A host that never calls AddAnvilboardRealtime still resolves a publisher, so mutations
        // have one code path whether or not a transport exists. TryAdd keeps AddAnvilboardRealtime
        // (and tests) free to register a real publisher first.
        services.TryAddSingleton<IRealtimeUpdatePublisher, NullRealtimeUpdatePublisher>();
        services.TryAddSingleton<IPluginEventPublisher, NullPluginEventPublisher>();
        services.TryAddScoped(_ => CorrelationContext.FromHeaderOrNew(null));

        return services;
    }

    /// <summary>
    /// Registers the bounded coalescing realtime pipeline and its background dispatcher. Split out
    /// from <see cref="AddAnvilboardApplication"/> for the same reason as the sync coordinator: a
    /// one-shot CLI invocation must not start a long-running loop just to create an issue.
    /// A host that wants events actually delivered also registers an <see cref="IRealtimeTransport"/>
    /// (the API host registers the SignalR one); otherwise changes are coalesced and discarded.
    /// </summary>
    public static IServiceCollection AddAnvilboardRealtime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Validate(
                options => options.QueueCapacity > 0,
                "Realtime:QueueCapacity must be greater than zero.")
            .Validate(
                options => options.DebounceWindow >= TimeSpan.Zero,
                "Realtime:DebounceWindow must not be negative.")
            .Validate(
                options => options.SendTimeout > TimeSpan.Zero,
                "Realtime:SendTimeout must be greater than zero.")
            .Validate(
                options => options.ShutdownFlushTimeout > TimeSpan.Zero,
                "Realtime:ShutdownFlushTimeout must be greater than zero.");

        services.AddMetrics();
        services.TryAddSingleton<RealtimeMetrics>();
        services.TryAddSingleton<RealtimeDispatchSignal>();
        services.TryAddSingleton(provider =>
            new RealtimeChangeBuffer(provider.GetRequiredService<IOptions<RealtimeOptions>>().Value.QueueCapacity));
        services.TryAddSingleton<IRealtimeTransport, NullRealtimeTransport>();

        // Replace rather than TryAdd: AddAnvilboardApplication may already have installed the no-op
        // default, and enabling realtime must not silently keep publishing into it.
        services.RemoveAll<IRealtimeUpdatePublisher>();
        services.AddSingleton<IRealtimeUpdatePublisher, CoalescingRealtimeUpdatePublisher>();
        services.AddHostedService<RealtimeDispatcher>();

        // Plugin events ride the same coalescing pipeline as committed mutations, so a plugin gets
        // no transport, buffering, or authorization path of its own (AC-RT-006). Replaces rather
        // than TryAdds for the same reason as the publisher above: the no-op default may already be
        // registered, and enabling realtime must not silently keep dropping plugin events.
        services.AddSingleton<PluginEventRelay>(provider => new PluginEventRelay(
            provider.GetRequiredService<IRealtimeUpdatePublisher>(),
            provider.GetRequiredService<IOptions<RealtimeOptions>>().Value,
            provider.GetRequiredService<ILogger<PluginEventRelay>>()));
        services.AddSingleton<ITrustedPluginEventPublisher>(provider => provider.GetRequiredService<PluginEventRelay>());

        // Reflection-loaded plugins have no host-authenticated workspace context, so their public
        // publisher remains a no-op. Host code uses the trusted publisher after it resolves routing.
        services.RemoveAll<IPluginEventPublisher>();
        services.AddSingleton<IPluginEventPublisher, PublicPluginEventPublisher>();

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
