using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;
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
/// <remarks>
/// <para>
/// A failing source backs off exponentially instead of hammering the provider at a fixed cadence
/// forever, and every attempt — success or failure — is recorded against the matching
/// <see cref="Integration"/> rows so the board can answer "is this data fresh?".
/// See <c>docs/plans/integration-sync-health.md</c> §8.3.
/// </para>
/// <para>
/// Attribution is fan-out (DR-ISH-002): a plugin's loop has no notion of which integration row it
/// is acting for, so one attempt writes the same outcome to every non-removed integration whose
/// provider maps to that plugin key. Threading an integration id through
/// <see cref="IIngestionSource"/> would be a breaking change to a public plugin contract for a
/// distinction that does not exist in the single-integration-per-provider deployments this
/// supports.
/// </para>
/// </remarks>
public sealed class SyncCoordinator(
    IPluginRegistry plugins,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<IngestionOptions> optionsMonitor,
    TimeProvider timeProvider,
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
        var pluginKey = source.Manifest.Key;
        var provider = ProviderFor(pluginKey);
        var backoff = BackoffState.Reset;

        // Logged once per loop, not per iteration: an unconfigured plugin is a steady state, and a
        // warning every poll interval would drown the log it is meant to be visible in.
        var warnedAboutMissingIntegration = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var options = optionsMonitor.Get(pluginKey);
                if (!options.Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var targets = await ResolveTargetsAsync(provider, stoppingToken);

                if (targets.Count == 0)
                {
                    if (!warnedAboutMissingIntegration)
                    {
                        logger.LogInformation(
                            "Plugin {PluginKey} is enabled but has no integration record; sync runs " +
                            "without health tracking", pluginKey);
                        warnedAboutMissingIntegration = true;
                    }
                }
                else if (targets.All(target => target.Status == IntegrationStatus.Paused))
                {
                    // FR-INT-001 AC3, poll side: a paused integration must stop pulling, not just
                    // stop accepting pushes, or "paused" would only be half true.
                    await Task.Delay(options.PollInterval, stoppingToken);
                    continue;
                }

                if (backoff.NotBefore is { } notBefore && notBefore > timeProvider.GetUtcNow())
                {
                    await DelayUntilAsync(notBefore, stoppingToken);
                    continue;
                }

                var attemptedAt = timeProvider.GetUtcNow();
                bool succeeded;
                SyncErrorCategory? category;

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var issueService = scope.ServiceProvider.GetRequiredService<IssueService>();

                    await foreach (var normalized in source.SyncAsync(cursor, stoppingToken))
                    {
                        await issueService.UpsertFromExternalUnscopedAsync(normalized, stoppingToken);
                        cursor = new SyncCursor(normalized.SyncFingerprint ?? cursor.Token);
                    }

                    succeeded = true;
                    category = null;
                    backoff = BackoffState.Reset;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    succeeded = false;
                    var failureCategory = Categorize(ex);
                    category = failureCategory;
                    backoff = Advance(backoff, options, failureCategory, ex, timeProvider.GetUtcNow());

                    // The exception message stays in the log and never reaches the health row: a
                    // provider error string can contain a token, and health is readable by actors
                    // who hold no access to integration secrets.
                    logger.LogError(
                        ex,
                        "Ingestion sync failed for plugin {PluginKey} ({Category}); attempt {FailureCount}, next attempt after {NextAttempt}",
                        pluginKey, category, backoff.ConsecutiveFailures, backoff.NotBefore);
                }

                await RecordOutcomesAsync(
                    targets, pluginKey, attemptedAt, succeeded, category, backoff.NotBefore, cursor.Token, stoppingToken);

                var wait = backoff.NotBefore is { } next
                    ? Clamp(next - timeProvider.GetUtcNow(), options.PollInterval)
                    : options.PollInterval;

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Resolves the integration rows one plugin's attempt is attributed to. Removed integrations
    /// are excluded; paused ones are kept so the caller can distinguish "nothing configured" from
    /// "everything paused", which lead to different loop behaviour.
    /// </summary>
    private async Task<IReadOnlyList<SyncTarget>> ResolveTargetsAsync(
        IntegrationProvider provider, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var rows = await db.Integrations
            .AsNoTracking()
            .Where(integration => integration.Provider == provider
                && integration.Status != IntegrationStatus.Removed)
            .Select(integration => new { integration.Id, integration.WorkspaceId, integration.Status })
            .ToListAsync(ct);

        return [.. rows.Select(row => new SyncTarget(row.Id, row.WorkspaceId, row.Status))];
    }

    private async Task RecordOutcomesAsync(
        IReadOnlyList<SyncTarget> targets,
        string pluginKey,
        DateTimeOffset attemptedAt,
        bool succeeded,
        SyncErrorCategory? category,
        DateTimeOffset? nextAttemptNotBefore,
        string? cursorToken,
        CancellationToken ct)
    {
        // Paused targets are excluded: they did not participate in the attempt, so recording its
        // outcome against them would attribute another integration's failure to them.
        var recordable = targets.Where(target => target.Status != IntegrationStatus.Paused).ToArray();
        if (recordable.Length == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();

        foreach (var target in recordable)
        {
            try
            {
                await health.RecordAttemptAsync(
                    new SyncAttemptOutcome(
                        target.IntegrationId,
                        target.WorkspaceId,
                        pluginKey,
                        attemptedAt,
                        succeeded,
                        category,
                        nextAttemptNotBefore,
                        succeeded ? cursorToken : null),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Telemetry must never take the sync loop down: failing to record that a sync
                // worked is strictly less bad than stopping syncing.
                logger.LogWarning(
                    ex, "Failed to record sync health for integration {IntegrationId}", target.IntegrationId);
            }
        }
    }

    /// <summary>
    /// Coarse failure classification. Plugins that throw the
    /// <see cref="ProviderSyncException"/> family get precise categories; anything else falls back
    /// to transport-or-unknown, which is the conservative choice because it keeps retrying.
    /// </summary>
    internal static SyncErrorCategory Categorize(Exception exception) => exception switch
    {
        ProviderAuthenticationException => SyncErrorCategory.Auth,
        ProviderThrottledException => SyncErrorCategory.RateLimited,
        HttpRequestException => SyncErrorCategory.Transport,
        TaskCanceledException or TimeoutException => SyncErrorCategory.Transport,
        _ => SyncErrorCategory.Unknown,
    };

    /// <summary>
    /// Computes the next backoff state after a failure.
    /// </summary>
    /// <remarks>
    /// Three shapes, because three failure modes deserve different treatment: a provider that
    /// stated a <c>Retry-After</c> is obeyed verbatim (it knows better than any local guess), a
    /// rejected credential is quarantined on a flat long interval (exponential growth is pointless
    /// when no amount of waiting fixes it), and everything else grows exponentially with full
    /// jitter so sources that failed together do not re-synchronize on recovery.
    /// </remarks>
    internal static BackoffState Advance(
        BackoffState current,
        IngestionOptions options,
        SyncErrorCategory category,
        Exception exception,
        DateTimeOffset now)
    {
        var failures = Math.Min(current.ConsecutiveFailures + 1, IntegrationHealth.MaxConsecutiveFailureCount);

        var delay = exception is ProviderThrottledException throttled && throttled.RetryAfter > TimeSpan.Zero
            ? throttled.RetryAfter
            : category == SyncErrorCategory.Auth
                ? options.QuarantineInterval
                : Delay(failures, options.PollInterval, options.MaxBackoff);

        return new BackoffState(failures, now + delay);
    }

    /// <summary>
    /// Exponential backoff with full jitter over <c>[0, 2^(n-1) × base]</c>, capped at
    /// <paramref name="max"/>.
    /// </summary>
    internal static TimeSpan Delay(int n, TimeSpan baseDelay, TimeSpan max)
    {
        var floor = baseDelay > TimeSpan.Zero ? baseDelay : TimeSpan.FromSeconds(1);
        var ceiling = max > floor ? max : floor;
        var shifted = floor.Ticks * (1L << Math.Min(Math.Max(n - 1, 0), 16));
        var bound = Math.Min(shifted <= 0 ? ceiling.Ticks : shifted, ceiling.Ticks);

        return TimeSpan.FromTicks(Random.Shared.NextInt64(1, Math.Max(bound, 2)));
    }

    private async Task DelayUntilAsync(DateTimeOffset instant, CancellationToken ct)
    {
        var remaining = instant - timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct);
        }
    }

    private static TimeSpan Clamp(TimeSpan candidate, TimeSpan fallback) =>
        candidate > TimeSpan.Zero ? candidate : fallback;

    /// <summary>
    /// Maps a plugin key onto the provider enum its integration rows use. Unknown keys are
    /// <see cref="IntegrationProvider.Custom"/>, which is what a third-party plugin registers as.
    /// </summary>
    internal static IntegrationProvider ProviderFor(string pluginKey) => pluginKey switch
    {
        "github" => IntegrationProvider.GitHub,
        "linear" => IntegrationProvider.Linear,
        _ => IntegrationProvider.Custom,
    };

    /// <summary>Per-loop retry state. Not persisted: a restart is itself a reason to retry now.</summary>
    internal readonly record struct BackoffState(int ConsecutiveFailures, DateTimeOffset? NotBefore)
    {
        public static BackoffState Reset => new(0, null);
    }

    private readonly record struct SyncTarget(
        IntegrationId IntegrationId, WorkspaceId WorkspaceId, IntegrationStatus Status);
}
