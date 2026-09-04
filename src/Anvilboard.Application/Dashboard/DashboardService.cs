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
public sealed class DashboardService(AnvilboardDbContext db)
{
    public async Task<DashboardSummary> GetSummaryAsync(TeamId? teamId = null, CancellationToken ct = default)
    {
        var query = db.Issues.AsNoTracking().AsQueryable();
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

        return new DashboardSummary(byStatus, bySource, createdLast7Days, completedLast7Days, byAssignee);
    }
}

public sealed record DashboardSummary(
    IReadOnlyDictionary<IssueStatus, int> IssuesByStatus,
    IReadOnlyDictionary<IntegrationProvider, int> IssuesBySource,
    int CreatedLast7Days,
    int CompletedLast7Days,
    IReadOnlyList<AssigneeLoad> OpenIssuesByAssignee);

public sealed record AssigneeLoad(MemberId AssigneeId, int OpenIssueCount);
