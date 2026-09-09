using Anvilboard.Api.Authorization;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Minimal setup endpoints for the entities a workspace needs before issues can be filed: teams
/// and members. Kept here directly against <see cref="AnvilboardDbContext"/> (rather than through
/// an application service) since these are simple CRUD operations with no hooks/events to
/// dispatch, unlike <see cref="Anvilboard.Application.Issues.IssueService"/>. The workspace to
/// scope every query/mutation to is always the caller's authenticated
/// <see cref="ActorContext.WorkspaceId"/> — no route creates or falls back to a "Default"
/// workspace; that only ever happens once, via the bootstrap flow (see `AuthEndpoints`).
/// </summary>
public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/teams").WithTags("Teams").RequirePermission(Permission.ReadBoard);

        teams.MapGet("/", async (HttpContext http, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            return Results.Ok(await db.Teams.AsNoTracking().Where(t => t.WorkspaceId == workspaceId).ToListAsync(ct));
        });

        teams.MapPost("/", async (CreateTeamRequest request, HttpContext http, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            var team = new Team
            {
                Id = TeamId.New(),
                WorkspaceId = workspaceId,
                Name = request.Name,
                Key = request.Key.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Teams.Add(team);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/teams/{team.Id.Value}", team);
        }).RequirePermission(Permission.ManageWorkspaceConfig);

        var members = app.MapGroup("/api/members").WithTags("Members").RequirePermission(Permission.ReadBoard);

        members.MapGet("/", async (HttpContext http, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            return Results.Ok(await db.Members.AsNoTracking().Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct));
        });

        members.MapPost("/", async (CreateMemberRequest request, HttpContext http, AnvilboardDbContext db, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            var member = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspaceId,
                DisplayName = request.DisplayName,
                Email = request.Email,
                IsAgent = request.IsAgent,
            };
            db.Members.Add(member);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/members/{member.Id.Value}", member);
        }).RequirePermission(Permission.ManageWorkspaceConfig);
    }
}

public sealed record CreateTeamRequest(string Name, string Key);
public sealed record CreateMemberRequest(string DisplayName, string? Email, bool IsAgent = false);
