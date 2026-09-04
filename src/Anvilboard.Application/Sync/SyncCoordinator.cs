using Anvilboard.Application.Issues;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Sync;

/// <summary>
/// Background service that drives every registered <see cref="IIngestionSource"/> plugin
/// (first-class GitHub/Linear, or any third-party plugin loaded into
/// <see cref="IPluginRegistry"/>) on its own polling interval and upserts whatever it yields via
/// <see cref="IssueService.UpsertFromExternalAsync"/>. Runs one independent timer loop per plugin
/// so a slow or failing source never delays another's polling cadence.
/// </summary>
public sealed class SyncCoordinator(
    IPluginRegistry plugins,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<IngestionOptions> optionsMonitor,
    ILogger<SyncCoordinator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = plugins.IngestionSources.Select(source => RunSourceLoopAsync(source, stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task RunSourceLoopAsync(IIngestionSource source, CancellationToken stoppingToken)
    {
        var cursor = SyncCursor.Empty;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var options = optionsMonitor.Get(source.Manifest.Key);
                if (!options.Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var issueService = scope.ServiceProvider.GetRequiredService<IssueService>();

                    await foreach (var normalized in source.SyncAsync(cursor, stoppingToken))
                    {
                        await issueService.UpsertFromExternalAsync(normalized, stoppingToken);
                        cursor = new SyncCursor(normalized.SyncFingerprint ?? cursor.Token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed sync run must not crash the host or stop future polling attempts.
                    logger.LogError(ex, "Ingestion sync failed for plugin {PluginKey}", source.Manifest.Key);
                }

                await Task.Delay(options.PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
    }
}
