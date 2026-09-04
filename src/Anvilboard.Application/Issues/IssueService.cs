using System.Text.Json;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Anvilboard.Application.Issues;

/// <summary>
/// The single write/read path for issue mutations, used identically by the ASP.NET Core API
/// controllers/endpoints and by the CLI/MCP agent operations
/// (<c>Anvilboard.Agent</c> wraps these same methods with <c>[AgentOperation]</c>), so a human
/// using the web UI and an agent using the CLI or MCP tool observe exactly the same behavior,
/// validation, and hook dispatch.
/// </summary>
public sealed class IssueService(
    AnvilboardDbContext db,
    IPluginRegistry plugins,
    ILogger<IssueService> logger)
{
    public async Task<Issue?> GetAsync(IssueId id, CancellationToken ct = default) =>
        await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Issue>> ListAsync(
        TeamId? teamId = null,
        IssueStatus? status = null,
        MemberId? assigneeId = null,
        CancellationToken ct = default)
    {
        var query = db.Issues.AsNoTracking().AsQueryable();
        if (teamId is { } team) query = query.Where(i => i.TeamId == team);
        if (status is { } s) query = query.Where(i => i.Status == s);
        if (assigneeId is { } assignee) query = query.Where(i => i.AssigneeId == assignee);

        // Sorted client-side: SQLite's EF provider cannot translate ORDER BY over a DateTimeOffset
        // column, and at this project's target scale (a single team/workspace) materializing the
        // filtered set first is cheap.
        var results = await query.ToListAsync(ct);
        return results.OrderByDescending(i => i.CreatedAt).ToList();
    }

    /// <summary>Creates an issue directly (source = Local), minting the next "TEAM-N" key.</summary>
    public async Task<Issue> CreateAsync(
        TeamId teamId,
        string title,
        string? description = null,
        IssuePriority priority = IssuePriority.None,
        ProjectId? projectId = null,
        MemberId? assigneeId = null,
        MemberId? createdById = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct)
            ?? throw new InvalidOperationException($"Team {teamId} does not exist.");

        var number = team.NextIssueNumber++;
        var now = DateTimeOffset.UtcNow;
        var issue = new Issue
        {
            Id = IssueId.New(),
            TeamId = teamId,
            ProjectId = projectId,
            Key = $"{team.Key}-{number}",
            Title = title,
            Description = description,
            Priority = priority,
            AssigneeId = assigneeId,
            CreatedById = createdById,
            Source = IntegrationProvider.Local,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Issues.Add(issue);
        await db.SaveChangesAsync(ct);

        await RecordAndDispatchAsync(issue, ActivityEventType.Created, actorId: createdById, data: null, ct);
        return issue;
    }

    /// <summary>Moves an issue to a new workflow status, recording the transition and firing hooks.</summary>
    public async Task<Issue> ChangeStatusAsync(IssueId id, IssueStatus newStatus, MemberId? actorId = null, CancellationToken ct = default)
    {
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException($"Issue {id} does not exist.");

        if (issue.Status == newStatus)
        {
            return issue;
        }

        var oldStatus = issue.Status;
        issue.Status = newStatus;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        if (newStatus.IsTerminal())
        {
            issue.CompletedAt ??= issue.UpdatedAt;
        }
        else
        {
            issue.CompletedAt = null;
        }

        await db.SaveChangesAsync(ct);

        var data = JsonSerializer.Serialize(new { from = oldStatus.ToString(), to = newStatus.ToString() });
        await RecordAndDispatchAsync(issue, ActivityEventType.StatusChanged, actorId, data, ct);
        return issue;
    }

    public async Task<Issue> AssignAsync(IssueId id, MemberId? assigneeId, MemberId? actorId = null, CancellationToken ct = default)
    {
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException($"Issue {id} does not exist.");

        issue.AssigneeId = assigneeId;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await RecordAndDispatchAsync(issue, ActivityEventType.AssigneeChanged, actorId, data: null, ct);
        return issue;
    }

    public async Task<Comment> AddCommentAsync(IssueId id, string body, MemberId? authorId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException($"Issue {id} does not exist.");

        var comment = new Comment
        {
            Id = CommentId.New(),
            IssueId = id,
            AuthorId = authorId,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        await RecordAndDispatchAsync(issue, ActivityEventType.CommentAdded, authorId, data: null, ct);
        return comment;
    }

    /// <summary>
    /// Upserts an issue synced in from an ingestion plugin, matching on the
    /// (Provider, SourceKey) dedupe key from <see cref="NormalizedIssue"/>. Used by
    /// <see cref="SyncCoordinator"/> for both first-class (GitHub/Linear) and third-party plugins.
    /// </summary>
    public async Task<Issue> UpsertFromExternalAsync(NormalizedIssue normalized, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Key == normalized.TeamKey, ct)
            ?? throw new InvalidOperationException($"No local team with key '{normalized.TeamKey}' to file synced issue under.");

        var link = await db.ExternalLinks.FirstOrDefaultAsync(
            l => l.Provider == normalized.Provider && l.SourceKey == normalized.SourceKey, ct);

        Issue issue;
        var now = DateTimeOffset.UtcNow;
        if (link is null)
        {
            var number = team.NextIssueNumber++;
            issue = new Issue
            {
                Id = IssueId.New(),
                TeamId = team.Id,
                Key = $"{team.Key}-{number}",
                Title = normalized.Title,
                Description = normalized.Description,
                Status = normalized.SuggestedStatus,
                Priority = normalized.SuggestedPriority,
                Source = normalized.Provider,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Issues.Add(issue);

            db.ExternalLinks.Add(new ExternalLink
            {
                Id = ExternalLinkId.New(),
                IssueId = issue.Id,
                Provider = normalized.Provider,
                SourceKey = normalized.SourceKey,
                Url = normalized.Url,
                SyncFingerprint = normalized.SyncFingerprint,
                LastSyncedAt = now,
            });

            await db.SaveChangesAsync(ct);
            await RecordAndDispatchAsync(issue, ActivityEventType.SyncedFromExternal, actorId: null, data: null, ct);
            return issue;
        }

        if (link.SyncFingerprint is not null && link.SyncFingerprint == normalized.SyncFingerprint)
        {
            // No-op: the remote item hasn't changed since the last successful sync.
            return await db.Issues.AsNoTracking().FirstAsync(i => i.Id == link.IssueId, ct);
        }

        issue = await db.Issues.FirstAsync(i => i.Id == link.IssueId, ct);
        issue.Title = normalized.Title;
        issue.Description = normalized.Description;
        issue.UpdatedAt = now;
        link.Url = normalized.Url;
        link.SyncFingerprint = normalized.SyncFingerprint;
        link.LastSyncedAt = now;

        await db.SaveChangesAsync(ct);
        await RecordAndDispatchAsync(issue, ActivityEventType.SyncedFromExternal, actorId: null, data: null, ct);
        return issue;
    }

    private async Task RecordAndDispatchAsync(Issue issue, ActivityEventType type, MemberId? actorId, string? data, CancellationToken ct)
    {
        var activityEvent = new ActivityEvent
        {
            Id = ActivityEventId.New(),
            IssueId = issue.Id,
            Type = type,
            ActorId = actorId,
            DataJson = data,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.ActivityEvents.Add(activityEvent);
        await db.SaveChangesAsync(ct);

        // Hooks run after the write has committed and are best-effort: a failing or slow plugin
        // must never roll back or block the mutation that triggered it (see IIssueHook remarks).
        var context = new IssueHookContext(issue, activityEvent);
        await Task.WhenAll(plugins.IssueHooks.Select(hook => InvokeHookSafelyAsync(hook, context, ct)));
    }

    private async Task InvokeHookSafelyAsync(IIssueHook hook, IssueHookContext context, CancellationToken ct)
    {
        try
        {
            await hook.OnIssueChangedAsync(context, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Issue hook {PluginKey} failed for issue {IssueKey}", hook.Manifest.Key, context.Issue.Key);
        }
    }
}
