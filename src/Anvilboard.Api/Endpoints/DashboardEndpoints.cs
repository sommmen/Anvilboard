using Anvilboard.Application.Dashboard;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard/summary", async (DashboardService service, Guid? teamId, CancellationToken ct) =>
        {
            var summary = await service.GetSummaryAsync(teamId is { } t ? new TeamId(t) : null, ct);
            return Results.Ok(summary);
        }).WithTags("Dashboard");
    }
}
