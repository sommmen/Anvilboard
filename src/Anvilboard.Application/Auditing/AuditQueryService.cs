using System.Text;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Auditing;

/// <summary>
/// The read path for the workspace audit trail (MAJ-018). <c>AuditEvent</c> rows have been written
/// on every security-sensitive action since the audit milestone but had no reader, so the trail was
/// recorded and never surfaced. This service is that reader; see
/// <c>docs/plans/audit-query-surface.md</c> for the full design.
/// </summary>
public sealed class AuditQueryService(AnvilboardDbContext db) : IAuditQueryService
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;

    /// <summary>
    /// <c>AuditEvents</c> grows forever and is never scoped to a single small entity the way
    /// <c>ActivityEvents</c> is scoped to one issue, so the set materialized for in-memory ordering
    /// (see below) must itself be bounded. If a filter still matches more than this many rows after
    /// the workspace predicate (and any other database-translatable predicates) run in the database,
    /// the service refuses to guess at a partial, possibly mis-ordered page and instead asks the
    /// caller to narrow the time range. The time-range predicate itself is applied in memory, after
    /// this ceiling check, because the SQLite provider cannot translate a relational comparison
    /// against a <c>DateTimeOffset</c> column — only equality translates — so a workspace whose full
    /// history exceeds the ceiling still needs a narrower time range to page through, even though the
    /// ceiling check runs before that range is applied.
    /// `ScanCeiling / MaximumLimit` = 25 full pages, which comfortably covers interactive
    /// investigation while bounding worst-case memory at a few thousand rows. Recorded as
    /// `DR-AUD-003`; the durable fix (a sortable, index-friendly ordering column) is `DR-AUD-006`.
    /// </summary>
    public const int ScanCeiling = 5_000;

    public async Task<AuditQueryResult> QueryAsync(
        WorkspaceId workspaceId, AuditQuery query, CancellationToken ct = default)
    {
        var limit = query.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaximumLimit)
        {
            throw new AuditQueryException("VALIDATION_FAILED", $"Limit must be between 1 and {MaximumLimit}.");
        }

        if (query.OccurredAfter is { } after && query.OccurredBefore is { } before && after >= before)
        {
            throw new AuditQueryException("VALIDATION_FAILED", "occurredAfter must be before occurredBefore.");
        }

        var offset = DecodeCursor(query.Cursor);

        var actorId = Normalize(query.ActorId);
        var targetType = Normalize(query.TargetType);
        var targetId = Normalize(query.TargetId);
        var action = Normalize(query.Action);

        // The workspace predicate is applied first and unconditionally, before any caller-supplied
        // filter, so no later clause can widen it beyond this workspace's own history.
        var filtered = db.AuditEvents.AsNoTracking().Where(e => e.WorkspaceId == workspaceId);

        if (actorId is not null)
        {
            filtered = filtered.Where(e => e.ActorId == actorId);
        }

        if (targetType is not null)
        {
            filtered = filtered.Where(e => e.TargetType == targetType);
        }

        if (targetId is not null)
        {
            filtered = filtered.Where(e => e.TargetId == targetId);
        }

        if (action is not null)
        {
            filtered = filtered.Where(e => e.Action == action);
        }

        if (query.Channel is { } channel)
        {
            filtered = filtered.Where(e => e.Channel == channel);
        }

        // The ceiling is checked before ordering: one extra row beyond it means the filter matched
        // more history than can be safely materialized and ordered in memory (see ScanCeiling doc).
        var scanned = await filtered.Take(ScanCeiling + 1).ToListAsync(ct);
        if (scanned.Count > ScanCeiling)
        {
            throw new AuditQueryException(
                "VALIDATION_FAILED",
                $"This filter matches more than {ScanCeiling} audit events. Narrow the time range and try again.");
        }

        // The time-range filter runs here, over the already-bounded in-memory set, rather than as a
        // database Where clause: the SQLite provider cannot translate a >=/< comparison against
        // DateTimeOffset (only equality is supported), the same underlying limitation that forces
        // ORDER BY below to run in memory too. If a workspace's history is dense enough that the
        // ScanCeiling is hit before this filter narrows it, the error above already tells the caller
        // to narrow the time range — the same remedy this filter would have applied.
        if (query.OccurredAfter is { } occurredAfter)
        {
            scanned = scanned.Where(e => e.OccurredAt >= occurredAfter).ToList();
        }

        if (query.OccurredBefore is { } occurredBefore)
        {
            scanned = scanned.Where(e => e.OccurredAt < occurredBefore).ToList();
        }

        // Ordering happens after materialization because the SQLite provider refuses to translate
        // ORDER BY over a DateTimeOffset — its TEXT representation carries an offset, so a lexical
        // sort would misorder rows written from different zones (see ActivityQueryService for the
        // identical precedent). Id is a total tiebreak: without it, two events recorded in the same
        // mutation share OccurredAt and the offset cursor could repeat or skip a row between pages.
        var page = scanned
            .OrderByDescending(e => e.OccurredAt)
            .ThenBy(e => e.Id.Value)
            .Skip(offset)
            .Take(limit + 1)
            .ToList();

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var events = page
            .Select(e => new AuditEventDto(
                e.Id.Value,
                e.ActorId,
                e.Channel,
                e.Action,
                e.TargetType,
                e.TargetId,
                e.CorrelationId,
                e.OccurredAt,
                e.ResultSummary))
            .ToList();

        var appliedQuery = query with { Limit = limit };

        return new AuditQueryResult(
            events,
            hasMore,
            hasMore ? EncodeCursor(offset + events.Count) : null,
            appliedQuery);
    }

    /// <summary>Whitespace-only filters are treated as absent, not as a match for empty string.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            throw new AuditQueryException("VALIDATION_FAILED", "The cursor is not valid.");
        }

        const string prefix = "offset:";

        return decoded.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(decoded[prefix.Length..], out var offset)
            && offset >= 0
            ? offset
            : throw new AuditQueryException("VALIDATION_FAILED", "The cursor is not valid.");
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"offset:{offset}"));
}
