using Anvilboard.Application.Auditing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Sync;

/// <summary>
/// Default <see cref="IIntegrationHealthService"/>, backed by the <c>IntegrationHealth</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The staleness threshold is per-plugin (<see cref="IngestionOptions.EffectiveStalenessThreshold"/>)
/// because a five-minute poller and a nightly importer are stale at wildly different ages; reading
/// it from the same options the coordinator polls with keeps the two from drifting.
/// </para>
/// <para>
/// Audit events are written on <b>transitions only</b>. A failing provider polled every five
/// minutes would otherwise write 288 identical audit rows a day and bury the one event an operator
/// actually needs to see — the transition into failure.
/// </para>
/// </remarks>
public sealed class IntegrationHealthService(
    AnvilboardDbContext db,
    IOptionsMonitor<IngestionOptions> ingestionOptions,
    IAuditService audit,
    TimeProvider timeProvider) : IIntegrationHealthService
{
    private const string SystemActorId = "system:sync-coordinator";

    public async Task RecordAttemptAsync(SyncAttemptOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Succeeded && outcome.ErrorCategory is not null)
        {
            throw new ArgumentException(
                "A successful attempt must not carry an error category.", nameof(outcome));
        }

        var integration = await db.Integrations
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == outcome.IntegrationId, ct);

        if (integration is null)
        {
            // The integration was removed between the attempt starting and finishing. Writing
            // health for a row that no longer exists would violate the foreign key and, worse,
            // resurrect a condition for something an administrator deliberately deleted.
            return;
        }

        var threshold = ingestionOptions.Get(outcome.PluginKey).EffectiveStalenessThreshold;
        var now = timeProvider.GetUtcNow();

        var health = await db.IntegrationHealth
            .SingleOrDefaultAsync(h => h.IntegrationId == outcome.IntegrationId, ct);

        var wasFailed = health is not null && IIntegrationHealthService.DeriveCondition(
            integration.Status,
            health.LastAttemptAt,
            health.LastSuccessAt,
            health.LastErrorCategory,
            threshold,
            now) == SyncCondition.Failed;

        if (health is null)
        {
            health = new IntegrationHealth
            {
                Id = IntegrationHealthId.New(),
                IntegrationId = outcome.IntegrationId,
                WorkspaceId = outcome.WorkspaceId,
                PluginKey = outcome.PluginKey,
            };
            db.IntegrationHealth.Add(health);
        }

        health.LastAttemptAt = outcome.AttemptedAt;
        health.NextAttemptNotBefore = outcome.NextAttemptNotBefore;
        health.UpdatedAt = now;

        if (outcome.Succeeded)
        {
            health.LastSuccessAt = outcome.AttemptedAt;
            health.LastErrorCategory = null;
            health.ConsecutiveFailureCount = 0;
            health.LastCursorToken = outcome.CursorToken ?? health.LastCursorToken;
        }
        else
        {
            health.LastErrorCategory = outcome.ErrorCategory ?? SyncErrorCategory.Unknown;
            health.ConsecutiveFailureCount = Math.Min(
                health.ConsecutiveFailureCount + 1, IntegrationHealth.MaxConsecutiveFailureCount);
        }

        await db.SaveChangesAsync(ct);

        var isFailed = IIntegrationHealthService.DeriveCondition(
            integration.Status,
            health.LastAttemptAt,
            health.LastSuccessAt,
            health.LastErrorCategory,
            threshold,
            now) == SyncCondition.Failed;

        if (isFailed == wasFailed)
        {
            return;
        }

        await audit.RecordAsync(new AuditEventRequest(
            outcome.WorkspaceId,
            SystemActorId,
            AuditChannel.System,
            isFailed ? "integration.sync.failed" : "integration.sync.recovered",
            "integration",
            outcome.IntegrationId.Value.ToString(),
            $"sync-{outcome.IntegrationId.Value:N}-{outcome.AttemptedAt.UtcTicks}",
            isFailed
                // Deliberately the category and the counter, never the provider's error message:
                // a provider that echoes a token back in an error string must not be able to
                // write it into the audit log through this path.
                ? $"{integration.Provider} sync failing ({health.LastErrorCategory}), " +
                  $"{health.ConsecutiveFailureCount} consecutive failure(s)"
                : $"{integration.Provider} sync recovered"), ct);
    }

    public async Task<IReadOnlyList<IntegrationHealthDto>> GetHealthAsync(
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();

        var rows = await db.Integrations
            .AsNoTracking()
            .Where(integration => integration.WorkspaceId == workspaceId
                && integration.Status != IntegrationStatus.Removed)
            .GroupJoin(
                db.IntegrationHealth.AsNoTracking(),
                integration => integration.Id,
                health => health.IntegrationId,
                (integration, health) => new { Integration = integration, Health = health })
            .SelectMany(
                row => row.Health.DefaultIfEmpty(),
                (row, health) => new { row.Integration, Health = health })
            .ToListAsync(ct);

        return [.. rows
            .Select(row => ToDto(row.Integration, row.Health, now))
            .OrderBy(dto => dto.Provider)
            .ThenBy(dto => dto.PluginKey, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlySet<IntegrationProvider>> ProvidersInConditionAsync(
        WorkspaceId workspaceId,
        SyncCondition condition,
        CancellationToken ct = default)
    {
        var health = await GetHealthAsync(workspaceId, ct);

        return health
            .Where(dto => dto.Condition == condition)
            .Select(dto => dto.Provider)
            .ToHashSet();
    }

    private IntegrationHealthDto ToDto(Integration integration, IntegrationHealth? health, DateTimeOffset now)
    {
        // An integration that has never been polled has no health row at all. It is reported as
        // stale rather than omitted: "we have never successfully synced this" is precisely the
        // answer a freshness question wants, and silently dropping it would read as healthy.
        var pluginKey = health?.PluginKey ?? PluginKeyFor(integration.Provider);
        var threshold = ingestionOptions.Get(pluginKey).EffectiveStalenessThreshold;

        var condition = IIntegrationHealthService.DeriveCondition(
            integration.Status,
            health?.LastAttemptAt,
            health?.LastSuccessAt,
            health?.LastErrorCategory,
            threshold,
            now);

        return new IntegrationHealthDto(
            integration.Id.Value,
            integration.Provider,
            pluginKey,
            integration.Status,
            condition,
            health?.LastAttemptAt,
            health?.LastSuccessAt,
            health?.LastErrorCategory,
            health?.ConsecutiveFailureCount ?? 0,
            health?.NextAttemptNotBefore);
    }

    /// <summary>
    /// Best-effort plugin key for an integration that has never produced a health row. Only used
    /// to pick an options section, so a miss costs a default threshold, not correctness.
    /// </summary>
    private static string PluginKeyFor(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.GitHub => "github",
        IntegrationProvider.Linear => "linear",
        _ => provider.ToString().ToLowerInvariant(),
    };
}
