using System.Text.Json;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Realtime;
using Anvilboard.Application.Workflows;
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
    IWorkflowService workflowService,
    IRealtimeUpdatePublisher realtimePublisher,
    CorrelationContext correlationContext,
    ILogger<IssueService> logger)
{
    private static IssueStatus? ToLegacyIssueStatus(string workflowStateKey) => workflowStateKey switch
    {
        "backlog" => IssueStatus.Backlog,
        "todo" => IssueStatus.Todo,
        "in_progress" => IssueStatus.InProgress,
        "in_review" => IssueStatus.InReview,
        "done" => IssueStatus.Done,
        "cancelled" => IssueStatus.Cancelled,
        _ => null,
    };

    /// <summary>
    /// Reads a single issue, scoped to <paramref name="workspaceId"/>. An issue outside that
    /// workspace is reported as absent rather than denied, so callers cannot use this method as an
    /// existence oracle for other tenants' ids.
    /// </summary>
    public async Task<Issue?> GetAsync(WorkspaceId workspaceId, IssueId id, CancellationToken ct = default) =>
        await db.Issues.AsNoTracking()
            .InWorkspace(db, workspaceId)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Issue>> ListAsync(
        WorkspaceId workspaceId,
        TeamId? teamId = null,
        IssueStatus? status = null,
        MemberId? assigneeId = null,
        CancellationToken ct = default)
    {
        // Workspace scoping is applied before any caller-supplied filter so an omitted or
        // attacker-chosen filter can only ever narrow an already-scoped set, never widen it.
        var query = db.Issues.AsNoTracking().InWorkspace(db, workspaceId);
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
        WorkspaceId workspaceId,
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

        // The workspace predicate is part of the lookup rather than a check afterwards: rejecting
        // later would still have advanced `team.NextIssueNumber` below, burning an issue number on
        // a create that never happened.
        var team = await db.Teams
            .FirstOrDefaultAsync(t => t.Id == teamId && t.WorkspaceId == workspaceId, ct)
            ?? throw new InvalidOperationException($"Team {teamId} does not exist.");

        var workflowStateId = await GetInitialWorkflowStateIdAsync(team.WorkspaceId, ct);
        var number = team.NextIssueNumber++;
        var now = DateTimeOffset.UtcNow;
        var issue = new Issue
        {
            Id = IssueId.New(),
            TeamId = teamId,
            WorkflowStateId = workflowStateId,
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

        await RecordAndDispatchAsync(
            issue,
            ActivityEventType.Created,
            actorId: createdById,
            data: null,
            ct,
            team.WorkspaceId,
            RealtimeIssueChangeKind.Created);
        return issue;
    }

    /// <summary>
    /// Moves an issue to a workspace-configured workflow state, delegating legality of the
    /// transition to <see cref="IWorkflowService.ValidateTransitionAsync"/> before mutating
    /// anything, then recording the transition and firing hooks. The deprecated
    /// <see cref="IssueStatus"/> projection is updated only when the target is one of the six
    /// seeded compatibility states.
    /// </summary>
    /// <exception cref="WorkflowTransitionDeniedException">
    /// The requested transition was denied by <see cref="IWorkflowService"/> (e.g. no configured
    /// transition rule, or a referenced workflow state is archived/missing).
    /// </exception>
    public async Task<Issue> ChangeStatusAsync(
        WorkspaceId workspaceId, IssueId id, WorkflowStateId targetStateId, MemberId? actorId = null, CancellationToken ct = default)
    {
        var issue = await db.Issues.InWorkspace(db, workspaceId).FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException($"Issue {id} does not exist.");

        if (issue.WorkflowStateId == targetStateId)
        {
            return issue;
        }

        var targetState = await db.WorkflowStates.AsNoTracking().FirstOrDefaultAsync(
            state => state.WorkspaceId == workspaceId && state.Id == targetStateId, ct)
            ?? throw new WorkflowTransitionDeniedException(
                "REFERENCED_ENTITY_NOT_FOUND",
                $"Workspace {workspaceId} has no workflow state with id '{targetStateId}'.");

        var validation = await workflowService.ValidateTransitionAsync(
            workspaceId, issue.WorkflowStateId, targetState.Id, ct);
        if (!validation.IsAllowed)
        {
            throw new WorkflowTransitionDeniedException(validation.ErrorCode!, validation.Message!);
        }

        var currentState = await db.WorkflowStates.AsNoTracking().FirstOrDefaultAsync(
            state => state.WorkspaceId == workspaceId && state.Id == issue.WorkflowStateId, ct);
        var oldStateKey = currentState?.Key ?? issue.Status.ToString();

        var legacyStatus = ToLegacyIssueStatus(targetState.Key);
        if (legacyStatus is { } status)
        {
            issue.Status = status;
        }
        issue.WorkflowStateId = targetState.Id;
        issue.Version++;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        if (targetState.IsTerminal)
        {
            issue.CompletedAt ??= issue.UpdatedAt;
        }
        else
        {
            issue.CompletedAt = null;
        }

        await db.SaveChangesAsync(ct);

        var data = JsonSerializer.Serialize(new { from = oldStateKey, to = targetState.Key });
        await RecordAndDispatchAsync(
            issue, ActivityEventType.StatusChanged, actorId, data, ct, workspaceId);
        return issue;
    }

    public async Task<Issue> AssignAsync(
        WorkspaceId workspaceId, IssueId id, MemberId? assigneeId, MemberId? actorId = null, CancellationToken ct = default)
    {
        var issue = await db.Issues.InWorkspace(db, workspaceId).FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException($"Issue {id} does not exist.");

        issue.AssigneeId = assigneeId;
        issue.Version++;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await RecordAndDispatchAsync(issue, ActivityEventType.AssigneeChanged, actorId, data: null, ct, workspaceId);
        return issue;
    }

    public async Task<Comment> AddCommentAsync(
        WorkspaceId workspaceId, IssueId id, string body, MemberId? authorId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var issue = await db.Issues.InWorkspace(db, workspaceId).FirstOrDefaultAsync(i => i.Id == id, ct)
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

        await RecordAndDispatchAsync(issue, ActivityEventType.CommentAdded, authorId, data: null, ct, workspaceId);
        return comment;
    }

    /// <summary>
    /// Upserts an issue synced in from an ingestion plugin, matching on the
    /// (Provider, SourceKey) dedupe key from <see cref="NormalizedIssue"/>. Used by
    /// <see cref="SyncCoordinator"/> for both first-class (GitHub/Linear) and third-party plugins.
    /// </summary>
    /// <remarks>
    /// Deliberately unscoped, and named to say so. Polling ingestion runs on a background timer with
    /// no authenticated actor, so there is no workspace to scope to; the team is resolved from
    /// <see cref="NormalizedIssue.TeamKey"/> instead and the overload fails closed when that key is
    /// ambiguous host-wide. Every caller that <em>does</em> have a workspace — trusted webhooks —
    /// must use <see cref="UpsertFromExternalAsync(WorkspaceId, NormalizedIssue, CancellationToken)"/>.
    /// </remarks>
    public Task<Issue> UpsertFromExternalUnscopedAsync(NormalizedIssue normalized, CancellationToken ct = default) =>
        UpsertFromExternalCoreAsync(normalized, workspaceId: null, ct);

    /// <summary>Upserts a trusted webhook issue into the specified workspace.</summary>
    public Task<Issue> UpsertFromExternalAsync(
        WorkspaceId workspaceId,
        NormalizedIssue normalized,
        CancellationToken ct = default) =>
        UpsertFromExternalCoreAsync(normalized, workspaceId, ct);

    private async Task<Issue> UpsertFromExternalCoreAsync(
        NormalizedIssue normalized,
        WorkspaceId? workspaceId,
        CancellationToken ct)
    {
        var teams = db.Teams.Where(t => t.Key == normalized.TeamKey);
        if (workspaceId is not null)
        {
            teams = teams.Where(t => t.WorkspaceId == workspaceId.Value);
        }

        // Team keys are only unique within a workspace (`TeamConfiguration` enforces
        // (WorkspaceId, Key)), so an unscoped lookup — the path `SyncCoordinator` uses for
        // first-class/plugin ingestion polling, which has no workspace to disambiguate with —
        // can match more than one team. `SingleOrDefaultAsync` would surface that as an
        // unhandled, unhelpfully-worded framework `InvalidOperationException`; count explicitly
        // instead so both the "missing" and "ambiguous" cases fail closed with a clear message
        // that a caller such as `SyncCoordinator`'s catch-and-log wrapper can log meaningfully.
        var matchingTeams = await teams.Take(2).ToListAsync(ct);
        var team = matchingTeams.Count switch
        {
            0 => throw new InvalidOperationException($"No local team with key '{normalized.TeamKey}' to file synced issue under."),
            1 => matchingTeams[0],
            _ => throw new InvalidOperationException(
                $"Team key '{normalized.TeamKey}' exists in more than one workspace; cannot determine which one to file synced issue under without an explicit workspace. Rename one of the teams so the key is unique host-wide."),
        };

        var link = await db.ExternalLinks.FirstOrDefaultAsync(
            l => l.Provider == normalized.Provider && l.SourceKey == normalized.SourceKey, ct);

        if (link is not null && workspaceId is not null)
        {
            // `ExternalLink` dedupes by (Provider, SourceKey) globally by design (see
            // ExternalLinkConfiguration), so an existing link can belong to a different workspace
            // than the one this trusted webhook resolved. Filing the update under the wrong
            // workspace's team would silently mutate another tenant's issue and misroute the
            // realtime activity/notification to this delivery's workspace instead of the owning
            // one, so verify the linked issue's team actually belongs to `workspaceId` first.
            var linkedIssueWorkspaceId = await db.Issues
                .Where(i => i.Id == link.IssueId)
                .Join(db.Teams, i => i.TeamId, t => t.Id, (i, t) => (WorkspaceId?)t.WorkspaceId)
                .SingleOrDefaultAsync(ct);

            if (linkedIssueWorkspaceId is not null && linkedIssueWorkspaceId.Value != workspaceId.Value)
            {
                throw new InvalidOperationException(
                    $"External link for {normalized.Provider}/{normalized.SourceKey} belongs to a different workspace than the trusted webhook target; refusing to apply a cross-tenant update.");
            }
        }

        Issue issue;
        var now = DateTimeOffset.UtcNow;
        if (link is null)
        {
            var workflowStateId = await GetInitialWorkflowStateIdAsync(team.WorkspaceId, ct);
            var number = team.NextIssueNumber++;
            issue = new Issue
            {
                Id = IssueId.New(),
                TeamId = team.Id,
                WorkflowStateId = workflowStateId,
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
            await RecordAndDispatchAsync(
                issue,
                ActivityEventType.SyncedFromExternal,
                actorId: null,
                data: null,
                ct,
                team.WorkspaceId,
                RealtimeIssueChangeKind.Created);
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
        issue.Version++;
        issue.UpdatedAt = now;
        link.Url = normalized.Url;
        link.SyncFingerprint = normalized.SyncFingerprint;
        link.LastSyncedAt = now;

        await db.SaveChangesAsync(ct);
        await RecordAndDispatchAsync(
            issue, ActivityEventType.SyncedFromExternal, actorId: null, data: null, ct, team.WorkspaceId);
        return issue;
    }

    private async Task<WorkflowStateId> GetInitialWorkflowStateIdAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        var initialState = await db.WorkflowStates
            .Where(state => state.WorkspaceId == workspaceId && !state.IsArchived)
            .OrderBy(state => state.Order)
            .ThenBy(state => state.Key)
            .Select(state => (WorkflowStateId?)state.Id)
            .FirstOrDefaultAsync(ct);

        return initialState ?? throw new InvalidOperationException(
            $"Workspace {workspaceId} has no active workflow state for new issues.");
    }

    private async Task RecordAndDispatchAsync(
        Issue issue,
        ActivityEventType type,
        MemberId? actorId,
        string? data,
        CancellationToken ct,
        WorkspaceId? workspaceId = null,
        RealtimeIssueChangeKind changeKind = RealtimeIssueChangeKind.Updated)
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

        // Realtime publication is post-commit for the same reason hooks are: the mutation is already
        // durable, so presentation delivery must never be able to undo or delay it (AC-RT-001).
        await PublishRealtimeAsync(issue, activityEvent, workspaceId, changeKind, ct);

        // Hooks run after the write has committed and are best-effort: a failing or slow plugin
        // must never roll back or block the mutation that triggered it (see IIssueHook remarks).
        var context = new IssueHookContext(issue, activityEvent);
        await Task.WhenAll(plugins.IssueHooks.Select(hook => InvokeHookSafelyAsync(hook, context, ct)));
    }

    /// <summary>
    /// Emits exactly one issue change and one activity change for the committed mutation. Failures
    /// are swallowed deliberately: realtime is a presentation convenience, and the caller already
    /// holds a committed result that must be returned successfully either way.
    /// </summary>
    private async Task PublishRealtimeAsync(
        Issue issue,
        ActivityEvent activityEvent,
        WorkspaceId? workspaceId,
        RealtimeIssueChangeKind changeKind,
        CancellationToken ct)
    {
        try
        {
            // Resolved inside the guarded region on purpose: as a caller-side argument this lookup
            // would run outside the catch below and could turn an already-committed mutation into
            // a 500 (AC-RT-001).
            var resolvedWorkspaceId = workspaceId ?? await ResolveWorkspaceIdAsync(issue.TeamId, ct);
            var correlationId = correlationContext.CorrelationId;
            var occurredAt = activityEvent.OccurredAt;

            await realtimePublisher.PublishAsync(
                new RealtimeIssueChange(
                    resolvedWorkspaceId, issue.Id, issue.Version, changeKind, correlationId, occurredAt),
                ct);
            await realtimePublisher.PublishAsync(
                new RealtimeActivityChange(
                    resolvedWorkspaceId, issue.Id, activityEvent.Id, issue.Version, correlationId, occurredAt),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Realtime publication failed for issue {IssueKey}; the mutation itself succeeded.", issue.Key);
        }
    }

    private async Task<WorkspaceId> ResolveWorkspaceIdAsync(TeamId teamId, CancellationToken ct) =>
        await db.Teams.AsNoTracking().Where(t => t.Id == teamId).Select(t => t.WorkspaceId).FirstAsync(ct);

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
