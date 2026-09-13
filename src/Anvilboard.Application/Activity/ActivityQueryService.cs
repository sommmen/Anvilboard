using System.Text;
using System.Text.Json;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Activity;

/// <summary>
/// The read path for the per-issue activity history. <c>ActivityEvent</c> rows have been written on
/// every meaningful mutation since the first milestone but had no reader, so the history was
/// recorded and never surfaced (MAJ-008). This service is that reader.
/// </summary>
public interface IActivityQueryService
{
    Task<ActivityPage> ListForIssueAsync(
        WorkspaceId workspaceId,
        IssueId issueId,
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default);
}

public sealed class ActivityQueryService(AnvilboardDbContext db) : IActivityQueryService
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;

    public async Task<ActivityPage> ListForIssueAsync(
        WorkspaceId workspaceId,
        IssueId issueId,
        int limit = DefaultLimit,
        string? cursor = null,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ActivityQueryException("VALIDATION_FAILED", $"Limit must be between 1 and {MaximumLimit}.");
        }

        var offset = DecodeCursor(cursor);

        // Scoped through the same team join every other issue read uses, so an activity feed can
        // never answer for an issue the caller's workspace cannot otherwise see.
        var reachable = await db.Issues.AsNoTracking()
            .InWorkspace(db, workspaceId)
            .AnyAsync(issue => issue.Id == issueId, ct);

        if (!reachable)
        {
            throw new ActivityQueryException(
                "REFERENCED_ENTITY_NOT_FOUND", "The issue was not found in this workspace.");
        }

        // Ordering happens after materialization because the SQLite provider refuses to translate
        // ORDER BY over a DateTimeOffset — its TEXT representation carries an offset, so a lexical
        // sort would misorder rows written from different zones. Filtering still runs in the
        // database against the indexed IssueId, so the materialized set is one issue's history,
        // the same bounded trade BoardQueryService and DashboardService already make.
        var all = await db.ActivityEvents.AsNoTracking()
            .Where(activityEvent => activityEvent.IssueId == issueId)
            .ToListAsync(ct);

        // Newest first, with Id as a total tiebreak: without it two events recorded in the same
        // mutation share OccurredAt and the offset cursor could repeat or skip a row between pages.
        // One extra row is taken purely to learn whether another page exists.
        var events = all
            .OrderByDescending(activityEvent => activityEvent.OccurredAt)
            .ThenBy(activityEvent => activityEvent.Id.Value)
            .Skip(offset)
            .Take(limit + 1)
            .ToList();

        var hasMore = events.Count > limit;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1);
        }

        // One batched lookup rather than a query per row: an issue with fifty events would
        // otherwise issue fifty member reads to render fifty names.
        var actorIds = events.Where(e => e.ActorId is not null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var actors = actorIds.Count == 0
            ? new Dictionary<MemberId, string>()
            : await db.Members.AsNoTracking()
                .Where(member => member.WorkspaceId == workspaceId && actorIds.Contains(member.Id))
                .ToDictionaryAsync(member => member.Id, member => member.DisplayName, ct);

        var entries = events
            .Select(activityEvent => new ActivityEntryDto(
                activityEvent.Id.Value,
                activityEvent.Type.ToString(),
                activityEvent.ActorId?.Value,
                activityEvent.ActorId is { } actorId && actors.TryGetValue(actorId, out var name) ? name : null,
                activityEvent.OccurredAt,
                ParseData(activityEvent.DataJson),
                Render(activityEvent, actors)))
            .ToList();

        return new ActivityPage(entries, hasMore ? EncodeCursor(offset + entries.Count) : null);
    }

    /// <summary>
    /// A flat, already-rendered sentence per event so a client that does not recognise a given
    /// event type can still display the row rather than dropping it.
    /// </summary>
    private static string Render(ActivityEvent activityEvent, IReadOnlyDictionary<MemberId, string> actors)
    {
        var actor = activityEvent.ActorId is { } id && actors.TryGetValue(id, out var name) ? name : "Someone";

        return activityEvent.Type switch
        {
            ActivityEventType.Created => $"{actor} created this issue",
            ActivityEventType.StatusChanged => $"{actor} changed the status",
            ActivityEventType.AssigneeChanged => $"{actor} changed the assignee",
            ActivityEventType.PriorityChanged => $"{actor} changed the priority",
            ActivityEventType.CommentAdded => $"{actor} commented",
            ActivityEventType.LabelsChanged => $"{actor} changed the labels",
            ActivityEventType.SyncedFromExternal => "Synced from the upstream provider",
            ActivityEventType.IssueLinkCreated => $"{actor} linked another issue",
            ActivityEventType.IssueLinkUpdated => $"{actor} updated an issue link",
            ActivityEventType.IssueLinkRemoved => $"{actor} removed an issue link",
            ActivityEventType.ArtifactAttached => $"{actor} attached an artifact",
            ActivityEventType.ArtifactRefreshed => $"{actor} refreshed an artifact",
            ActivityEventType.ArtifactRemoved => $"{actor} removed an artifact",
            _ => $"{actor} updated this issue",
        };
    }

    private static JsonElement? ParseData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            // Cloned because the JsonDocument backing a raw JsonElement is pooled and disposed here;
            // the returned element outlives this method.
            using var document = JsonDocument.Parse(dataJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A malformed payload written by an older or third-party writer must not take the whole
            // feed down — the rendered Text still carries the event.
            return null;
        }
    }

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
            throw new ActivityQueryException("VALIDATION_FAILED", "The cursor is not valid.");
        }

        const string prefix = "offset:";

        return decoded.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(decoded[prefix.Length..], out var offset)
            && offset >= 0
            ? offset
            : throw new ActivityQueryException("VALIDATION_FAILED", "The cursor is not valid.");
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"offset:{offset}"));
}

public sealed record ActivityEntryDto(
    Guid Id,
    string Type,
    Guid? ActorId,
    string? ActorDisplayName,
    DateTimeOffset OccurredAt,
    JsonElement? Data,
    string Text);

public sealed record ActivityPage(IReadOnlyList<ActivityEntryDto> Entries, string? NextCursor);

public sealed class ActivityQueryException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
