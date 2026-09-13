using Anvilboard.Api.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// The small reference lists the board's filter controls need to render: projects, labels, and the
/// suggested issue-link types. Without them a client can only offer a free-text box for values that
/// are actually identifiers, which is why the board filters could not be built before.
/// </summary>
public static class TaxonomyEndpoints
{
    public static void MapTaxonomyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", async (AnvilboardDbContext db, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            var workspaceId = scope.WorkspaceId;

            // Projects hang off a team rather than carrying a workspace, so scoping is the same
            // join RestWorkspaceScope.RequireProjectAsync uses.
            var projects = await db.Projects.AsNoTracking()
                .Join(db.Teams.AsNoTracking(), project => project.TeamId, team => team.Id,
                    (project, team) => new { Project = project, team.WorkspaceId })
                .Where(row => row.WorkspaceId == workspaceId)
                .OrderBy(row => row.Project.Name)
                .Select(row => new ProjectSummaryResponse(
                    row.Project.Id.Value, row.Project.TeamId.Value, row.Project.Name, row.Project.Description))
                .ToListAsync(ct);

            return Results.Ok(projects);
        }).WithTags("Taxonomy").RequirePermission(Permission.ReadBoard);

        app.MapGet("/api/labels", async (AnvilboardDbContext db, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            var workspaceId = scope.WorkspaceId;

            var labels = await db.Labels.AsNoTracking()
                .Where(label => label.WorkspaceId == workspaceId)
                .OrderBy(label => label.Name)
                .Select(label => new LabelSummaryResponse(label.Id.Value, label.Name, label.Color))
                .ToListAsync(ct);

            return Results.Ok(labels);
        }).WithTags("Taxonomy").RequirePermission(Permission.ReadBoard);

        // Static, workspace-independent, and advisory: link types are free-form strings, so this is
        // the suggestion list clients present rather than a constraint the server enforces.
        app.MapGet("/api/issue-link-types", () => Results.Ok(IssueLinkTypes.Suggested))
            .WithTags("Taxonomy")
            .RequirePermission(Permission.ReadBoard);
    }
}

public sealed record ProjectSummaryResponse(Guid Id, Guid TeamId, string Name, string? Description);

public sealed record LabelSummaryResponse(Guid Id, string Name, string Color);
