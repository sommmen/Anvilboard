using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Minimal setup endpoints for the entities a fresh workspace needs before issues can be filed:
/// workspaces, teams, and members. Kept here directly against <see cref="AnvilboardDbContext"/>
/// (rather than through an application service) since these are simple CRUD operations with no
/// hooks/events to dispatch, unlike <see cref="Anvilboard.Application.Issues.IssueService"/>.
/// </summary>
public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/teams").WithTags("Teams");

        teams.MapGet("/", async (AnvilboardDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Teams.AsNoTracking().ToListAsync(ct)));

        teams.MapPost("/", async (CreateTeamRequest request, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspace = await db.Workspaces.FirstOrDefaultAsync(ct);
            if (workspace is null)
            {
                workspace = new Workspace { Id = WorkspaceId.New(), Name = "Default", Slug = "default", CreatedAt = DateTimeOffset.UtcNow };
                db.Workspaces.Add(workspace);
            }

            var team = new Team
            {
                Id = TeamId.New(),
                WorkspaceId = workspace.Id,
                Name = request.Name,
                Key = request.Key.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Teams.Add(team);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/teams/{team.Id.Value}", team);
        });

        var members = app.MapGroup("/api/members").WithTags("Members");

        members.MapGet("/", async (AnvilboardDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Members.AsNoTracking().ToListAsync(ct)));

        members.MapPost("/", async (CreateMemberRequest request, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspace = await db.Workspaces.FirstOrDefaultAsync(ct);
            if (workspace is null)
            {
                workspace = new Workspace { Id = WorkspaceId.New(), Name = "Default", Slug = "default", CreatedAt = DateTimeOffset.UtcNow };
                db.Workspaces.Add(workspace);
            }

            var member = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspace.Id,
                DisplayName = request.DisplayName,
                Email = request.Email,
                IsAgent = request.IsAgent,
            };
            db.Members.Add(member);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/members/{member.Id.Value}", member);
        });
    }
}

public sealed record CreateTeamRequest(string Name, string Key);
public sealed record CreateMemberRequest(string DisplayName, string? Email, bool IsAgent = false);
