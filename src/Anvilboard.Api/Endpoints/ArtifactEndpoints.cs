using Anvilboard.Api.Authorization;
using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Artifact sub-resource routes on <c>/api/issues/{id}/artifacts</c>. Thin HTTP adapters over
/// <see cref="IArtifactService"/> — the same service the CLI/MCP agent surface calls directly, so
/// list ordering and error semantics cannot diverge between surfaces (AC-ART-103). Artifacts have
/// no permission of their own: they inherit the parent issue's (BR-ART-5).
/// </summary>
public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/issues").WithTags("Issues").RequirePermission(Permission.ReadBoard);

        group.MapGet("/{id:guid}/artifacts", async (Guid id, IArtifactService service, CancellationToken ct) =>
        {
            // An issue with no artifacts is a 200 with an empty list, never a 404: the issue exists.
            var artifacts = await service.ListArtifactsAsync(new IssueId(id), ct);
            return Results.Ok(artifacts);
        });

        group.MapPost("/{id:guid}/artifacts", async (
            Guid id, AttachArtifactRequest request, IArtifactService service, CancellationToken ct) =>
        {
            try
            {
                var artifact = await service.AttachArtifactAsync(
                    new IssueId(id),
                    request.Kind,
                    request.Title,
                    request.ContentReference,
                    request.Source,
                    request.ActorId is { } actor ? new MemberId(actor) : null,
                    request.Metadata,
                    AuditChannel.Rest,
                    ct);
                return Results.Created($"/api/issues/{id}/artifacts/{artifact.Id}", artifact);
            }
            catch (ArtifactException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCode(ex));
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapDelete("/{id:guid}/artifacts/{artifactId:guid}", async (
            Guid id, Guid artifactId, Guid? actorId, IArtifactService service, CancellationToken ct) =>
        {
            try
            {
                await service.RemoveArtifactAsync(
                    new IssueId(id),
                    new ArtifactId(artifactId),
                    actorId is { } actor ? new MemberId(actor) : null,
                    AuditChannel.Rest,
                    ct);
                return Results.NoContent();
            }
            catch (ArtifactException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCode(ex));
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);
    }

    private static int StatusCode(ArtifactException ex) => ex.ErrorCode switch
    {
        "REFERENCED_ENTITY_NOT_FOUND" => StatusCodes.Status404NotFound,
        // The store is a downstream dependency of this API, so its outage is a gateway failure
        // rather than a client error — the caller's request was well-formed.
        "ARTIFACT_STORE_UNAVAILABLE" => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// Reference-only attach. Uploading bytes over HTTP is a separate, later concern (it needs
/// multipart handling and a request size limit); this route takes a reference the caller has
/// already resolved.
/// </summary>
public sealed record AttachArtifactRequest(
    string Kind,
    string Title,
    string ContentReference,
    string? Source = null,
    Guid? ActorId = null,
    string? Metadata = null);
