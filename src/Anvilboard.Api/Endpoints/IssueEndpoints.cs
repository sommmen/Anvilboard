using Anvilboard.Application.Issues;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Issue CRUD/transition endpoints. Thin HTTP adapters over <see cref="IssueService"/> — the same
/// service the CLI/MCP agent surface calls directly, so behavior never diverges between a human
/// using the web UI and an agent using the board.
/// </summary>
public static class IssueEndpoints
{
    public static void MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/issues").WithTags("Issues");

        group.MapGet("/", async (IssueService service, Guid? teamId, IssueStatus? status, Guid? assigneeId, CancellationToken ct) =>
        {
            var issues = await service.ListAsync(
                teamId is { } t ? new TeamId(t) : null,
                status,
                assigneeId is { } a ? new MemberId(a) : null,
                ct);
            return Results.Ok(issues);
        });

        group.MapGet("/{id:guid}", async (Guid id, IssueService service, CancellationToken ct) =>
        {
            var issue = await service.GetAsync(new IssueId(id), ct);
            return issue is not null ? Results.Ok(issue) : Results.NotFound();
        });

        group.MapPost("/", async (CreateIssueRequest request, IssueService service, CancellationToken ct) =>
        {
            var issue = await service.CreateAsync(
                new TeamId(request.TeamId),
                request.Title,
                request.Description,
                request.Priority ?? IssuePriority.None,
                request.ProjectId is { } p ? new ProjectId(p) : null,
                request.AssigneeId is { } a ? new MemberId(a) : null,
                ct: ct);
            return Results.Created($"/api/issues/{issue.Id.Value}", issue);
        });

        group.MapPatch("/{id:guid}/status", async (Guid id, ChangeStatusRequest request, IssueService service, CancellationToken ct) =>
        {
            var issue = await service.ChangeStatusAsync(new IssueId(id), request.Status, ct: ct);
            return Results.Ok(issue);
        });

        group.MapPatch("/{id:guid}/assignee", async (Guid id, AssignRequest request, IssueService service, CancellationToken ct) =>
        {
            var issue = await service.AssignAsync(new IssueId(id), request.AssigneeId is { } a ? new MemberId(a) : null, ct: ct);
            return Results.Ok(issue);
        });

        group.MapPost("/{id:guid}/comments", async (Guid id, AddCommentRequest request, IssueService service, CancellationToken ct) =>
        {
            var comment = await service.AddCommentAsync(
                new IssueId(id), request.Body, request.AuthorId is { } a ? new MemberId(a) : null, ct);
            return Results.Created($"/api/issues/{id}/comments/{comment.Id.Value}", comment);
        });
    }
}

public sealed record CreateIssueRequest(Guid TeamId, string Title, string? Description, IssuePriority? Priority, Guid? ProjectId, Guid? AssigneeId);
public sealed record ChangeStatusRequest(IssueStatus Status);
public sealed record AssignRequest(Guid? AssigneeId);
public sealed record AddCommentRequest(string Body, Guid? AuthorId = null);
