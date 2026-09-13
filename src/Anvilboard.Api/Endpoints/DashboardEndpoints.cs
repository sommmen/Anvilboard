using Anvilboard.Api.Authorization;
using Anvilboard.Application.Dashboard;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard/summary", async (DashboardService service, RestWorkspaceScope scope, Guid? teamId, CancellationToken ct) =>
        {
            try
            {
                // The team filter is scoped too: an unscoped foreign team id would otherwise return
                // an all-zero summary, which still confirms whether that id exists.
                var team = teamId is { } t ? await scope.RequireTeamAsync(t, ct) : (TeamId?)null;
                var summary = await service.GetSummaryAsync(scope.WorkspaceId, team, ct: ct);
                return Results.Ok(summary);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        }).WithTags("Dashboard").RequirePermission(Permission.ReadDashboard);
    }
}
