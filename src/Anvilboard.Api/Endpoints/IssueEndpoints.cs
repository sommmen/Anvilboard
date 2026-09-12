using Anvilboard.Api.Authorization;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Issue CRUD/transition endpoints. Thin HTTP adapters over <see cref="IssueService"/> — the same
/// service the CLI/MCP agent surface calls directly, so behavior never diverges between a human
/// using the web UI and an agent using the board. Mutation routes accept either
/// <see cref="Permission.ReadWriteIssues"/> (`Coordinator`/`Administrator`, workspace-wide) or
/// <see cref="Permission.ReadWriteAssignedIssues"/> (`Contributor`, assigned/team-scoped) —
/// per-assignment/team scoping is a per-field business rule owned by <c>IssueService</c> itself,
/// not this component (§ Excluded).
/// </summary>
public static class IssueEndpoints
{
    public static void MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/issues").WithTags("Issues").RequirePermission(Permission.ReadBoard);

        group.MapGet("/", async (IssueService service, RestWorkspaceScope scope, Guid? teamId, IssueStatus? status, Guid? assigneeId, CancellationToken ct) =>
        {
            try
            {
                // The filters are scoped as well. The service would already exclude foreign rows, so
                // an unscoped foreign id returns an empty list -- but "empty" versus "denied" still
                // tells the caller whether that id exists, and it would make this route disagree
                // with the dashboard summary about the very same teamId.
                var team = teamId is { } t ? await scope.RequireTeamAsync(t, ct) : (TeamId?)null;
                var assignee = await scope.RequireMemberAsync(assigneeId, ct);
                var issues = await service.ListAsync(scope.WorkspaceId, team, status, assignee, ct: ct);
                return Results.Ok(issues);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        });

        group.MapGet("/{id:guid}", async (Guid id, IssueService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var issue = await service.GetAsync(scope.WorkspaceId, issueId, ct);
                return issue is not null ? Results.Ok(issue) : WorkspaceScopeResults.Denied();
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        });

        group.MapPost("/", async (CreateIssueRequest request, IssueService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var teamId = await scope.RequireTeamAsync(request.TeamId, ct);
                var assigneeId = await scope.RequireMemberAsync(request.AssigneeId, ct);
                var issue = await service.CreateAsync(
                    scope.WorkspaceId,
                    teamId,
                    request.Title,
                    request.Description,
                    request.Priority ?? IssuePriority.None,
                    request.ProjectId is { } p ? new ProjectId(p) : null,
                    assigneeId,
                    ct: ct);
                return Results.Created($"/api/issues/{issue.Id.Value}", issue);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapPatch("/{id:guid}/status", async (Guid id, ChangeStatusRequest request, IssueService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var issue = await service.ChangeStatusAsync(scope.WorkspaceId, issueId, request.Status, ct: ct);
                return Results.Ok(issue);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (WorkflowTransitionDeniedException ex)
            {
                // REFERENCED_ENTITY_NOT_FOUND here is a *dependent* lookup failing after scoping
                // already succeeded (a missing workflow state), so 404 is not an existence oracle.
                var statusCode = ex.ErrorCode == "REFERENCED_ENTITY_NOT_FOUND"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status409Conflict;
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: statusCode);
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapPatch("/{id:guid}/assignee", async (Guid id, AssignRequest request, IssueService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var assigneeId = await scope.RequireMemberAsync(request.AssigneeId, ct);
                var issue = await service.AssignAsync(scope.WorkspaceId, issueId, assigneeId, ct: ct);
                return Results.Ok(issue);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapPost("/{id:guid}/comments", async (Guid id, AddCommentRequest request, IssueService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var authorId = await scope.RequireMemberAsync(request.AuthorId, ct);
                var comment = await service.AddCommentAsync(scope.WorkspaceId, issueId, request.Body, authorId, ct);
                return Results.Created($"/api/issues/{id}/comments/{comment.Id.Value}", comment);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        }).RequirePermission(Permission.ReadWriteComments);

        group.MapGet("/{id:guid}/links", async (Guid id, IssueLinkService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var links = await service.ListLinksAsync(scope.WorkspaceId, issueId, ct);
                return Results.Ok(links);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
        });

        group.MapPost("/{id:guid}/links", async (Guid id, CreateIssueLinkRequest request, IssueLinkService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var targetIssueId = await scope.RequireIssueAsync(request.TargetIssueId, ct);
                var actorId = await scope.RequireMemberAsync(request.ActorId, ct);
                var link = await service.CreateLinkAsync(
                    scope.WorkspaceId,
                    issueId,
                    targetIssueId,
                    request.Type,
                    request.Description,
                    actorId,
                    ct);
                return Results.Created($"/api/issues/{id}/links/{link.Id}", link);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (IssueLinkException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: ex.ErrorCode switch
                {
                    "REFERENCED_ENTITY_NOT_FOUND" => StatusCodes.Status404NotFound,
                    "RESOURCE_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest,
                });
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapDelete("/{id:guid}/links/{linkId:guid}", async (Guid id, Guid linkId, Guid? actorId, IssueLinkService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var actor = await scope.RequireMemberAsync(actorId, ct);
                await service.RemoveLinkAsync(scope.WorkspaceId, issueId, new IssueLinkId(linkId), actor, ct);
                return Results.NoContent();
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (IssueLinkException ex)
            {
                // The issue itself already scoped cleanly; only the link id can still be missing,
                // and a link id is not addressable across workspaces, so 404 discloses nothing.
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);
    }
}

public sealed record CreateIssueRequest(Guid TeamId, string Title, string? Description, IssuePriority? Priority, Guid? ProjectId, Guid? AssigneeId);
public sealed record ChangeStatusRequest(IssueStatus Status);
public sealed record AssignRequest(Guid? AssigneeId);
public sealed record AddCommentRequest(string Body, Guid? AuthorId = null);
public sealed record CreateIssueLinkRequest(Guid TargetIssueId, string Type, string? Description = null, Guid? ActorId = null);
