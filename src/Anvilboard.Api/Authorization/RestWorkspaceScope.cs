using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Authorization;

/// <summary>
/// Resolves caller-supplied entity identifiers against the authenticated request's workspace.
/// </summary>
/// <remarks>
/// <para>
/// The REST mirror of <c>AgentWorkspaceScope</c>. <see cref="WorkspaceAuthorizationMiddleware"/>
/// proves <em>who</em> the caller is and <em>what</em> they may do, but routes still take raw
/// <see cref="Guid"/> identifiers from the URL. Without this check a caller authenticated against
/// workspace A could pass an identifier belonging to workspace B and the request would succeed.
/// </para>
/// <para>
/// Unknown and out-of-workspace identifiers deliberately produce the same
/// <c>WORKSPACE_ACCESS_DENIED</c> response, byte for byte. Distinguishing them would turn every
/// ID-addressed route into an existence oracle for other workspaces' data — which is why an unknown
/// issue id now answers 403 rather than the 404 it used to.
/// </para>
/// </remarks>
public sealed class RestWorkspaceScope(AnvilboardDbContext db, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>The authenticated workspace for the request in flight.</summary>
    public WorkspaceId WorkspaceId => CurrentContext.WorkspaceId;

    private Application.Authorization.ActorContext CurrentContext =>
        (httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "RestWorkspaceScope resolved outside an HTTP request; it is only valid in a request scope."))
        .GetActorContext();

    /// <summary>Verifies a team belongs to the request's workspace.</summary>
    public async Task<TeamId> RequireTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var id = new TeamId(teamId);
        var workspaceId = WorkspaceId;

        var belongs = await db.Teams
            .AsNoTracking()
            .AnyAsync(team => team.Id == id && team.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new WorkspaceScopeDeniedException(
                $"Team {teamId} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies an issue belongs to the request's workspace, via its owning team.</summary>
    public async Task<IssueId> RequireIssueAsync(Guid issueId, CancellationToken ct = default)
    {
        var id = new IssueId(issueId);
        var workspaceId = WorkspaceId;

        // Shares WorkspaceScopedQueries with the services themselves, so "which workspace owns this
        // issue" cannot be answered one way at the boundary and another way underneath it.
        var belongs = await db.Issues
            .AsNoTracking()
            .InWorkspace(db, workspaceId)
            .AnyAsync(issue => issue.Id == id, ct);

        return belongs
            ? id
            : throw new WorkspaceScopeDeniedException(
                $"Issue {issueId} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies an optional member reference belongs to the request's workspace.</summary>
    public async Task<MemberId?> RequireMemberAsync(Guid? memberId, CancellationToken ct = default)
    {
        if (memberId is not { } value)
        {
            return null;
        }

        var id = new MemberId(value);
        var workspaceId = WorkspaceId;

        var belongs = await db.Members
            .AsNoTracking()
            .AnyAsync(member => member.Id == id && member.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new WorkspaceScopeDeniedException(
                $"Member {value} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies a workflow state belongs to the request's workspace.</summary>
    public async Task<WorkflowStateId> RequireWorkflowStateAsync(Guid stateId, CancellationToken ct = default)
    {
        var id = new WorkflowStateId(stateId);
        var workspaceId = WorkspaceId;

        var belongs = await db.WorkflowStates
            .AsNoTracking()
            .AnyAsync(state => state.Id == id && state.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new WorkspaceScopeDeniedException(
                $"Workflow state {stateId} is not accessible from the authenticated workspace.");
    }

    /// <summary>Verifies a workflow transition belongs to the request's workspace.</summary>
    public async Task<WorkflowTransitionId> RequireWorkflowTransitionAsync(
        Guid transitionId,
        CancellationToken ct = default)
    {
        var id = new WorkflowTransitionId(transitionId);
        var workspaceId = WorkspaceId;

        var belongs = await db.WorkflowTransitions
            .AsNoTracking()
            .AnyAsync(transition => transition.Id == id && transition.WorkspaceId == workspaceId, ct);

        return belongs
            ? id
            : throw new WorkspaceScopeDeniedException(
                $"Workflow transition {transitionId} is not accessible from the authenticated workspace.");
    }
}

/// <summary>
/// Raised when a caller-supplied identifier cannot be reached from the authenticated workspace.
/// </summary>
/// <remarks>
/// The message is for logs only. <see cref="WorkspaceScopeResults.Denied"/> is the single place that
/// turns this into a response, and it deliberately discloses nothing beyond the error code so the
/// foreign and nonexistent cases stay indistinguishable on the wire.
/// </remarks>
public sealed class WorkspaceScopeDeniedException(string message) : Exception(message);

/// <summary>The one REST rendering of a workspace-scope denial.</summary>
public static class WorkspaceScopeResults
{
    public const string AccessDenied = "WORKSPACE_ACCESS_DENIED";

    /// <summary>
    /// Mirrors <c>WorkspaceAuthorizationMiddleware.WriteDenialAsync</c>: title-only, no detail and
    /// no <c>data</c>, per AC-002/AC-103.
    /// </summary>
    public static IResult Denied() =>
        Results.Problem(title: AccessDenied, statusCode: StatusCodes.Status403Forbidden);
}
