using Anvilboard.Api.Authorization;
using Anvilboard.Api.Middleware;
using Anvilboard.Application.Sync;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// REST surface for integration sync health (<c>docs/plans/integration-sync-health.md</c> §9) —
/// the first enforcement site for <see cref="Permission.ReadIntegrationHealth"/>, which
/// <see cref="RolePermissionMap"/> grants to Administrators and Coordinators.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> under <see cref="Permission.ManageIntegrations"/>: answering "is this
/// board's data fresh?" is a question a coordinator needs to answer daily, whereas configuring an
/// integration's credentials is an administrative act. Separating them lets the routine question
/// be asked without handing out the ability to read or rewrite provider secrets.
/// </para>
/// <para>
/// The response body is <see cref="IntegrationHealthDto"/>, which structurally cannot carry
/// credential material — it exposes timings, a coarse error category, and a counter, never a
/// provider error message that might echo a token back.
/// </para>
/// </remarks>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations")
            .WithTags("Integrations")
            .RequirePermission(Permission.ReadIntegrationHealth);

        group.MapGet("/health", async (
            HttpContext http, IIntegrationHealthService health, CancellationToken ct) =>
        {
            // Workspace comes from the authenticated actor, never from the query string, so the
            // route cannot be used to read another tenant's integration state.
            var workspaceId = http.GetActorContext().WorkspaceId;
            return Results.Ok(await health.GetHealthAsync(workspaceId, ct));
        });
    }
}
