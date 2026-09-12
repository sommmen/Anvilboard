using Anvilboard.Agent.Automation;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Agent.Authorization;

/// <summary>
/// Resolves caller-supplied entity identifiers against the authenticated actor's workspace.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkspaceAuthorizationPolicy"/> proves <em>who</em> the caller is and <em>what</em>
/// they may do, but operations still take raw <see cref="Guid"/> identifiers. Rejecting foreign
/// identifiers here keeps a caller authenticated against workspace A from naming an entity in
/// workspace B, and reports the rejection in the agent's own error vocabulary rather than letting
/// it surface as an opaque application-layer failure.
/// </para>
/// <para>
/// Unknown and out-of-workspace identifiers deliberately produce the same
/// <c>WORKSPACE_ACCESS_DENIED</c> error. Distinguishing them would turn every operation into an
/// existence oracle for other workspaces' data.
/// </para>
/// <para>
/// This is one of two boundary guards: <c>Anvilboard.Api.Authorization.RestWorkspaceScope</c> is
/// the REST sibling. Both are defence in depth over the application services, which since MAJ-022
/// take a required leading workspace identifier and filter through
/// <c>WorkspaceScopedQueries.InWorkspace</c>.
/// </para>
/// </remarks>
public sealed class AgentWorkspaceScope(AnvilboardDbContext db, AgentActorAccessor actors)
{
    private const string AccessDenied = "WORKSPACE_ACCESS_DENIED";

    /// <summary>The authenticated workspace for the invocation in flight.</summary>
    public WorkspaceId WorkspaceId => actors.Actor.WorkspaceId;

    /// <summary>Verifies a team belongs to the actor's workspace.</summary>
    public async Task<TeamId> RequireTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var id = new TeamId(teamId);
        var workspaceId = actors.Actor.WorkspaceId;

        var belongs = await db.Teams
            .AsNoTracking()
            .AnyAsync(team => team.Id == id && team.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new AgentRequestException(
                AccessDenied, $"Team {teamId} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies an issue belongs to the actor's workspace, via its owning team.</summary>
    public async Task<IssueId> RequireIssueAsync(Guid issueId, CancellationToken ct = default)
    {
        var id = new IssueId(issueId);
        var workspaceId = actors.Actor.WorkspaceId;

        var belongs = await db.Issues
            .AsNoTracking()
            .Join(db.Teams.AsNoTracking(), issue => issue.TeamId, team => team.Id,
                (issue, team) => new { Issue = issue, team.WorkspaceId })
            .AnyAsync(row => row.Issue.Id == id && row.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new AgentRequestException(
                AccessDenied, $"Issue {issueId} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies an optional member reference belongs to the actor's workspace.</summary>
    public async Task<MemberId?> RequireMemberAsync(Guid? memberId, CancellationToken ct = default)
    {
        if (memberId is not { } value)
        {
            return null;
        }

        var id = new MemberId(value);
        var workspaceId = actors.Actor.WorkspaceId;

        var belongs = await db.Members
            .AsNoTracking()
            .AnyAsync(member => member.Id == id && member.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new AgentRequestException(
                AccessDenied, $"Member {value} is not accessible from the authenticated workspace.");
    }
}
