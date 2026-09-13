using Anvilboard.Application.Issues;
using Anvilboard.Application.Sync;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Dashboard;

/// <summary>
/// Read-only aggregation queries backing the dashboard UI. Deliberately implemented as plain
/// SQLite aggregate queries (no separate OLAP/warehouse) — at the single-workspace scale this
/// project targets, that keeps the "low-resource" story intact instead of adding a reporting
/// pipeline.
/// </summary>
public sealed class DashboardService(AnvilboardDbContext db, IIntegrationHealthService health)
{
    public async Task<DashboardSummary> GetSummaryAsync(
        WorkspaceId workspaceId,
        TeamId? teamId = null,
        CancellationToken ct = default)
    {
        // Scoped before the optional team filter, so an omitted `teamId` narrows to the workspace
        // rather than widening to every issue on the host.
        var query = db.Issues.AsNoTracking().InWorkspace(db, workspaceId);
        if (teamId is { } team) query = query.Where(i => i.TeamId == team);

        // Materialized once and aggregated in memory: SQLite's EF provider cannot translate several
        // DateTimeOffset comparisons/orderings used below, and at this project's target scale (a
        // single team/workspace) loading the filtered set is cheap and keeps every aggregate
        // consistent with a single point-in-time snapshot.
        var issues = await query.ToListAsync(ct);

        var byStatus = Enum.GetValues<IssueStatus>()
            .ToDictionary(s => s, s => issues.Count(i => i.Status == s));

        var bySource = issues
            .GroupBy(i => i.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        var sevenDaysAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var completedLast7Days = issues.Count(
            i => i.Status == IssueStatus.Done && i.CompletedAt is { } completed && completed >= sevenDaysAgo);
        var createdLast7Days = issues.Count(i => i.CreatedAt >= sevenDaysAgo);

        var byAssignee = issues
            .Where(i => i.AssigneeId is not null && i.Status != IssueStatus.Done && i.Status != IssueStatus.Cancelled)
            .GroupBy(i => i.AssigneeId!.Value)
            .Select(g => new AssigneeLoad(g.Key, g.Count()))
            .ToList();

        // Counts above are only trustworthy if the data behind them is current, so the summary
        // carries its own freshness caveat rather than leaving a reader to assume a stale board is
        // simply a quiet one.
        var integrationHealth = await health.GetHealthAsync(workspaceId, ct);
        var freshness = new IntegrationFreshness(
            integrationHealth.Count(dto => dto.Condition == SyncCondition.Fresh),
            integrationHealth.Count(dto => dto.Condition == SyncCondition.Stale),
            integrationHealth.Count(dto => dto.Condition == SyncCondition.Paused),
            integrationHealth.Count(dto => dto.Condition == SyncCondition.Failed),
            integrationHealth
                .Select(dto => dto.LastSuccessAt)
                .Where(at => at is not null)
                .DefaultIfEmpty(null)
                .Min());

        return new DashboardSummary(
            byStatus, bySource, createdLast7Days, completedLast7Days, byAssignee, freshness);
    }
}

public sealed record DashboardSummary(
    IReadOnlyDictionary<IssueStatus, int> IssuesByStatus,
    IReadOnlyDictionary<IntegrationProvider, int> IssuesBySource,
    int CreatedLast7Days,
    int CompletedLast7Days,
    IReadOnlyList<AssigneeLoad> OpenIssuesByAssignee,
    IntegrationFreshness IntegrationFreshness);

/// <summary>
/// How current the synced portion of the board is.
/// </summary>
/// <param name="Fresh">Integrations that synced successfully within their staleness threshold.</param>
/// <param name="Stale">Integrations with no recent successful sync.</param>
/// <param name="Paused">Integrations an administrator has paused.</param>
/// <param name="Failed">Integrations whose last attempt failed.</param>
/// <param name="OldestSuccessfulSyncAt">
/// The least recent successful sync across all integrations — the true age of the board's synced
/// data, since one lagging integration makes the whole picture that old. Null when nothing has
/// ever synced successfully.
/// </param>
public sealed record IntegrationFreshness(
    int Fresh,
    int Stale,
    int Paused,
    int Failed,
    DateTimeOffset? OldestSuccessfulSyncAt);

public sealed record AssigneeLoad(MemberId AssigneeId, int OpenIssueCount);
