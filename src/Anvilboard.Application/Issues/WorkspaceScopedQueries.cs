using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Application.Issues;

/// <summary>
/// Workspace scoping for issue queries, expressed once so every scoped read in the application
/// layer shares one definition of "this issue belongs to that workspace".
/// </summary>
public static class WorkspaceScopedQueries
{
    /// <summary>
    /// Constrains an issue query to one workspace. Issues carry no <c>WorkspaceId</c> of their own,
    /// so membership is reached through the owning team — this join <em>is</em> the definition of an
    /// issue belonging to a workspace, and keeping it in one place stops the rule drifting between
    /// the REST, agent, and sync surfaces.
    /// </summary>
    public static IQueryable<Issue> InWorkspace(
        this IQueryable<Issue> issues, AnvilboardDbContext db, WorkspaceId workspaceId) =>
        issues.Where(issue => db.Teams.Any(
            team => team.Id == issue.TeamId && team.WorkspaceId == workspaceId));
}
