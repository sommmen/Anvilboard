using System.Text.Json;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Issues;

/// <summary>
/// Provides the single mutation and read path for directional issue links. Link types are stored
/// as opaque strings, so this service never changes workflow state or otherwise interprets them.
/// </summary>
public sealed class IssueLinkService(AnvilboardDbContext db)
{
    public async Task<IssueLinkDto> CreateLinkAsync(
        WorkspaceId workspaceId,
        IssueId sourceIssueId,
        IssueId targetIssueId,
        string type,
        string? description = null,
        MemberId? actorId = null,
        CancellationToken ct = default)
    {
        if (sourceIssueId == targetIssueId)
        {
            throw new IssueLinkException("VALIDATION_FAILED", "An issue cannot be linked to itself.");
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new IssueLinkException("VALIDATION_FAILED", "Link type must not be empty.");
        }

        // Scoping both endpoints to the *caller's* workspace, not merely to each other: comparing
        // the two issues' workspaces alone would still let a caller link two issues that both
        // belong to some third tenant.
        var reachableIds = await db.Issues.AsNoTracking()
            .InWorkspace(db, workspaceId)
            .Where(issue => issue.Id == sourceIssueId || issue.Id == targetIssueId)
            .Select(issue => issue.Id)
            .ToListAsync(ct);

        if (!reachableIds.Contains(sourceIssueId) || !reachableIds.Contains(targetIssueId))
        {
            throw new IssueLinkException("REFERENCED_ENTITY_NOT_FOUND", "Both issues must exist in the same workspace.");
        }

        var normalizedType = type.Trim();
        if (await db.IssueLinks.AnyAsync(link =>
            link.SourceIssueId == sourceIssueId && link.TargetIssueId == targetIssueId && link.Type == normalizedType, ct))
        {
            throw new IssueLinkException("RESOURCE_ALREADY_EXISTS", "An identical directional issue link already exists.");
        }

        var link = new IssueLink
        {
            Id = IssueLinkId.New(),
            SourceIssueId = sourceIssueId,
            TargetIssueId = targetIssueId,
            Type = normalizedType,
            Description = description ?? string.Empty,
            CreatedById = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.IssueLinks.Add(link);
        await RecordActivityAsync(sourceIssueId, ActivityEventType.IssueLinkCreated, actorId, link, ct);
        await db.SaveChangesAsync(ct);

        return IssueLinkDto.FromLink(link, IssueLinkDirection.Outgoing);
    }

    public async Task<IReadOnlyList<IssueLinkDto>> ListLinksAsync(
        WorkspaceId workspaceId, IssueId issueId, CancellationToken ct = default)
    {
        await RequireIssueInWorkspaceAsync(workspaceId, issueId, ct);

        var links = await db.IssueLinks.AsNoTracking()
            .Where(link => link.SourceIssueId == issueId || link.TargetIssueId == issueId)
            .ToListAsync(ct);

        return links.OrderBy(link => link.CreatedAt)
            .Select(link => IssueLinkDto.FromLink(link, link.SourceIssueId == issueId
                ? IssueLinkDirection.Outgoing
                : IssueLinkDirection.Incoming))
            .ToList();
    }

    /// <summary>
    /// Corrects a link's type and/or description in place. Re-creating the link instead would reset
    /// <see cref="IssueLink.CreatedAt"/> and orphan the activity already recorded against it, so a
    /// mistyped link is edited rather than deleted and re-added (MAJ-011).
    /// </summary>
    public async Task<IssueLinkDto> UpdateLinkAsync(
        WorkspaceId workspaceId,
        IssueId issueId,
        IssueLinkId linkId,
        string? type = null,
        string? description = null,
        MemberId? actorId = null,
        CancellationToken ct = default)
    {
        if (type is null && description is null)
        {
            throw new IssueLinkException("VALIDATION_FAILED", "At least one of type or description must be supplied.");
        }

        if (type is not null && string.IsNullOrWhiteSpace(type))
        {
            throw new IssueLinkException("VALIDATION_FAILED", "Link type must not be empty.");
        }

        await RequireIssueInWorkspaceAsync(workspaceId, issueId, ct);

        var link = await db.IssueLinks.FirstOrDefaultAsync(candidate => candidate.Id == linkId, ct);
        if (link is null || (link.SourceIssueId != issueId && link.TargetIssueId != issueId))
        {
            throw new IssueLinkException("REFERENCED_ENTITY_NOT_FOUND", "The issue link was not found for this issue.");
        }

        var normalizedType = type?.Trim() ?? link.Type;

        // The same uniqueness rule CreateLinkAsync enforces: retyping a link must not be a back door
        // to a duplicate that the create path would have rejected.
        if (normalizedType != link.Type && await db.IssueLinks.AnyAsync(candidate =>
            candidate.Id != linkId
            && candidate.SourceIssueId == link.SourceIssueId
            && candidate.TargetIssueId == link.TargetIssueId
            && candidate.Type == normalizedType, ct))
        {
            throw new IssueLinkException("RESOURCE_ALREADY_EXISTS", "An identical directional issue link already exists.");
        }

        link.Type = normalizedType;
        link.Description = description ?? link.Description;

        // Both endpoints, unlike create/remove: an edit made from one side must still appear in the
        // other issue's feed, which is the only place the counterpart's watchers would see it.
        await RecordActivityAsync(link.SourceIssueId, ActivityEventType.IssueLinkUpdated, actorId, link, ct);
        await RecordActivityAsync(link.TargetIssueId, ActivityEventType.IssueLinkUpdated, actorId, link, ct);
        await db.SaveChangesAsync(ct);

        return IssueLinkDto.FromLink(link, link.SourceIssueId == issueId
            ? IssueLinkDirection.Outgoing
            : IssueLinkDirection.Incoming);
    }

    public async Task RemoveLinkAsync(
        WorkspaceId workspaceId, IssueId issueId, IssueLinkId linkId, MemberId? actorId = null, CancellationToken ct = default)
    {
        await RequireIssueInWorkspaceAsync(workspaceId, issueId, ct);

        var link = await db.IssueLinks.FirstOrDefaultAsync(candidate => candidate.Id == linkId, ct);
        if (link is null || (link.SourceIssueId != issueId && link.TargetIssueId != issueId))
        {
            throw new IssueLinkException("REFERENCED_ENTITY_NOT_FOUND", "The issue link was not found for this issue.");
        }

        db.IssueLinks.Remove(link);
        await RecordActivityAsync(issueId, ActivityEventType.IssueLinkRemoved, actorId, link, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fails with the same "not found" code whether the issue is foreign or absent, so link routes
    /// cannot be used to probe for the existence of another workspace's issue ids.
    /// </summary>
    private async Task RequireIssueInWorkspaceAsync(WorkspaceId workspaceId, IssueId issueId, CancellationToken ct)
    {
        var exists = await db.Issues.AsNoTracking()
            .InWorkspace(db, workspaceId)
            .AnyAsync(issue => issue.Id == issueId, ct);

        if (!exists)
        {
            throw new IssueLinkException("REFERENCED_ENTITY_NOT_FOUND", "The issue was not found in this workspace.");
        }
    }

    private async Task RecordActivityAsync(
        IssueId issueId,
        ActivityEventType type,
        MemberId? actorId,
        IssueLink link,
        CancellationToken ct)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            Id = ActivityEventId.New(),
            IssueId = issueId,
            Type = type,
            ActorId = actorId,
            DataJson = JsonSerializer.Serialize(new
            {
                sourceIssueId = link.SourceIssueId.Value,
                targetIssueId = link.TargetIssueId.Value,
                link.Type,
                link.Description,
            }),
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }
}

public enum IssueLinkDirection
{
    Outgoing,
    Incoming,
}

public sealed record IssueLinkDto(
    Guid Id,
    Guid SourceIssueId,
    Guid TargetIssueId,
    string Type,
    string Description,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    IssueLinkDirection Direction)
{
    public static IssueLinkDto FromLink(IssueLink link, IssueLinkDirection direction) => new(
        link.Id.Value,
        link.SourceIssueId.Value,
        link.TargetIssueId.Value,
        link.Type,
        link.Description,
        link.CreatedById?.Value,
        link.CreatedAt,
        direction);
}
