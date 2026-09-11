using Anvilboard.Application.Artifacts;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Backup;
using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent;

/// <summary>
/// Thin <see cref="AgentOperationAttribute"/>-annotated wrapper over <see cref="IssueService"/>,
/// <see cref="DashboardService"/>, and <see cref="IBackupService"/> — the same application services
/// the ASP.NET Core API host calls directly from its minimal API endpoints. Wrapping (rather than
/// annotating the application services themselves) keeps <c>Anvilboard.Application</c> free of any
/// dependency on the agent surface package, and lets this layer shape parameters/results (plain
/// scalars, no EF entities) the way <see cref="OperationCatalog.Discover"/>'s reflection-based
/// binding expects.
/// </summary>
public sealed class BoardAgentService(
    IssueService issues,
    IssueLinkService issueLinks,
    IArtifactService artifacts,
    DashboardService dashboard,
    IBackupService backups,
    CorrelationContext correlation)
{
    /// <summary>
    /// Actor id for backup/restore audit events raised through this unauthenticated CLI/MCP host
    /// (`docs/plans/backup-and-restore.md` §11.4: "the current unauthenticated agent host[...]
    /// non-destructive operations use its configured automation actor ID"). Follows the
    /// <c>"agent:{name}"</c> actor-id convention already used for automation actors elsewhere (see
    /// <c>Anvilboard.Application.Tests</c> audit fixtures), rather than inventing a new one.
    /// </summary>
    private const string AutomationActorId = "agent:automation";

    /// <summary>
    /// This process is both a CLI and an MCP server (see <c>Program.cs</c>), but never both at once
    /// for a single invocation — the transport is fixed for the process's whole lifetime by whether
    /// <c>args[0]</c> is <c>"mcp"</c>. Mirroring that exact check (rather than threading a new
    /// per-request signal through DI) lets every <see cref="BackupOperationContext"/> built here
    /// carry the real, explicit <see cref="AuditChannel"/> for the transport actually in use,
    /// consistent with plan §11.4's "must not infer the channel from runtime type or ambient
    /// process mode" — this reads the one ambient signal (argv) that Program.cs itself already
    /// treats as authoritative for choosing the transport, rather than guessing.
    /// </summary>
    private static readonly AuditChannel AgentChannel =
        Environment.GetCommandLineArgs() is [_, "mcp", ..] ? AuditChannel.Mcp : AuditChannel.Cli;

    private BackupOperationContext NewBackupOperationContext() =>
        new(AutomationActorId, AgentChannel, correlation.CorrelationId);


    [AgentOperation("list-issues", "Lists issues, optionally filtered by team, status, or assignee", Category = "issues", IsIdempotent = true)]
    public async Task<IReadOnlyList<IssueSummary>> ListIssuesAsync(
        Guid? teamId = null, IssueStatus? status = null, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var results = await issues.ListAsync(
            teamId is { } t ? new TeamId(t) : null,
            status,
            assigneeId is { } a ? new MemberId(a) : null,
            cancellationToken);
        return [.. results.Select(IssueSummary.FromIssue)];
    }

    [AgentOperation("get-issue", "Gets a single issue by id", Category = "issues", IsIdempotent = true)]
    public async Task<IssueSummary?> GetIssueAsync(Guid issueId, CancellationToken cancellationToken = default)
    {
        var issue = await issues.GetAsync(new IssueId(issueId), cancellationToken);
        return issue is null ? null : IssueSummary.FromIssue(issue);
    }

    [AgentOperation("create-issue", "Creates a new issue under a team", Category = "issues", Examples = ["create-issue teamId=... title=\"Forge the anvil\""])]
    public async Task<IssueSummary> CreateIssueAsync(
        Guid teamId,
        string title,
        string? description = null,
        IssuePriority priority = IssuePriority.None,
        Guid? assigneeId = null,
        CancellationToken cancellationToken = default)
    {
        var issue = await issues.CreateAsync(
            new TeamId(teamId), title, description, priority, projectId: null,
            assigneeId is { } a ? new MemberId(a) : null, createdById: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("change-issue-status", "Changes the status of an issue", Category = "issues")]
    public async Task<IssueSummary> ChangeIssueStatusAsync(Guid issueId, IssueStatus status, CancellationToken cancellationToken = default)
    {
        var issue = await issues.ChangeStatusAsync(new IssueId(issueId), status, actorId: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("assign-issue", "Assigns (or unassigns, when assigneeId is omitted) an issue", Category = "issues")]
    public async Task<IssueSummary> AssignIssueAsync(Guid issueId, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var issue = await issues.AssignAsync(
            new IssueId(issueId), assigneeId is { } a ? new MemberId(a) : null, actorId: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("comment-on-issue", "Adds a comment to an issue", Category = "issues")]
    public async Task<CommentSummary> CommentOnIssueAsync(Guid issueId, string body, CancellationToken cancellationToken = default)
    {
        var comment = await issues.AddCommentAsync(new IssueId(issueId), body, authorId: null, cancellationToken);
        return new CommentSummary(comment.Id.Value, comment.IssueId.Value, comment.AuthorId?.Value, comment.Body, comment.CreatedAt);
    }

    [AgentOperation("dashboard-summary", "Gets aggregate dashboard counts (by status, by source, velocity, workload)", Category = "dashboard", IsIdempotent = true)]
    public async Task<DashboardSummary> DashboardSummaryAsync(Guid? teamId = null, CancellationToken cancellationToken = default)
    {
        return await dashboard.GetSummaryAsync(teamId is { } t ? new TeamId(t) : null, cancellationToken);
    }

    [AgentOperation("list-issue-links", "Lists links involving an issue, in either direction", Category = "issues", IsIdempotent = true)]
    public async Task<IReadOnlyList<IssueLinkDto>> ListIssueLinksAsync(Guid issueId, CancellationToken cancellationToken = default) =>
        await issueLinks.ListLinksAsync(new IssueId(issueId), cancellationToken);

    [AgentOperation("create-issue-link", "Creates a directional, typed link from one issue to another", Category = "issues", Examples = ["create-issue-link issueId=... targetIssueId=... type=RELATED"])]
    public async Task<IssueLinkDto> CreateIssueLinkAsync(
        Guid issueId, Guid targetIssueId, string type, string? description = null, CancellationToken cancellationToken = default) =>
        await issueLinks.CreateLinkAsync(new IssueId(issueId), new IssueId(targetIssueId), type, description, actorId: null, cancellationToken);

    [AgentOperation("remove-issue-link", "Removes a link from an issue", Category = "issues")]
    public async Task RemoveIssueLinkAsync(Guid issueId, Guid linkId, CancellationToken cancellationToken = default) =>
        await issueLinks.RemoveLinkAsync(new IssueId(issueId), new IssueLinkId(linkId), actorId: null, cancellationToken);

    [AgentOperation("list-artifacts", "Lists the artifacts attached to an issue, oldest first", Category = "issues", IsIdempotent = true)]
    public async Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(Guid issueId, CancellationToken cancellationToken = default) =>
        await artifacts.ListArtifactsAsync(new IssueId(issueId), ct: cancellationToken);

    /// <summary>
    /// Attaches an already-addressable artifact. <paramref name="source"/> is required and
    /// <c>actorId</c> is always null: this host is unauthenticated (MAJ-001/MAJ-015), so its
    /// attachments are automation attachments and must be labelled as such (BR-ART-1/BR-ART-2)
    /// rather than borrowing a human identity.
    /// </summary>
    [AgentOperation("attach-artifact", "Attaches an artifact (file reference, link, deployment, or pull request) to an issue", Category = "issues", Examples = ["attach-artifact issueId=... kind=link title=\"CI run\" contentReference=https://ci.example/run/42 source=agent:automation"])]
    public async Task<ArtifactDto> AttachArtifactAsync(
        Guid issueId,
        string kind,
        string title,
        string contentReference,
        string source = AutomationActorId,
        CancellationToken cancellationToken = default) =>
        await artifacts.AttachArtifactAsync(
            new IssueId(issueId), kind, title, contentReference, source,
            actorId: null, metadata: null, AgentChannel, ct: cancellationToken);

    [AgentOperation("remove-artifact", "Removes an artifact from an issue and purges its stored content", Category = "issues")]
    public async Task RemoveArtifactAsync(Guid issueId, Guid artifactId, CancellationToken cancellationToken = default) =>
        await artifacts.RemoveArtifactAsync(
            new IssueId(issueId), new ArtifactId(artifactId), actorId: null, AgentChannel, ct: cancellationToken);

    // `refresh-artifact` is deliberately not exposed here (`docs/features/artifacts.md`, API Surface):
    // a pull request artifact's state must only ever reflect what the provider reports, so the
    // upsert path stays reachable from plugin correlation logic only. An agent that could call it
    // could assert a PR was merged when it was not.

    // `restore` is deliberately not exposed here (`docs/plans/backup-and-restore.md` §9.3): MAJ-015
    // records that agent/automation operations currently lack workspace-scoped authorization and
    // actor identity, so exposing an instance-wide destructive operation on this surface would be a
    // privilege escalation. Revisit once MAJ-015 closes (plan §17 OQ-P3).

    [AgentOperation("create-backup", "Creates a verified snapshot of the instance database",
        Category = "backup", Examples = ["create-backup workspaceId=..."])]
    public async Task<BackupManifest> CreateBackupAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        await backups.CreateBackupAsync(new WorkspaceId(workspaceId), NewBackupOperationContext(), cancellationToken);

    [AgentOperation("list-backups", "Lists available verified backups, newest first",
        Category = "backup", IsIdempotent = true)]
    public async Task<IReadOnlyList<BackupManifest>> ListBackupsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        await backups.ListBackupsAsync(new WorkspaceId(workspaceId), cancellationToken);

    [AgentOperation("verify-backup", "Verifies a backup's integrity without restoring it",
        Category = "backup", IsIdempotent = true, Examples = ["verify-backup workspaceId=... backupId=7f3c..."])]
    public async Task<BackupVerification> VerifyBackupAsync(Guid workspaceId, Guid backupId, CancellationToken cancellationToken = default) =>
        await backups.VerifyBackupAsync(new WorkspaceId(workspaceId), new BackupArtifactRef(backupId), cancellationToken);
}

public sealed record IssueSummary(
    Guid Id,
    Guid TeamId,
    string Key,
    string Title,
    string? Description,
    IssueStatus Status,
    IssuePriority Priority,
    Guid? AssigneeId,
    IntegrationProvider Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public static IssueSummary FromIssue(Issue issue) => new(
        issue.Id.Value, issue.TeamId.Value, issue.Key, issue.Title, issue.Description,
        issue.Status, issue.Priority, issue.AssigneeId?.Value, issue.Source,
        issue.CreatedAt, issue.UpdatedAt, issue.CompletedAt);
}

public sealed record CommentSummary(Guid Id, Guid IssueId, Guid? AuthorId, string Body, DateTimeOffset CreatedAt);
