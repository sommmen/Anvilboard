using Anvilboard.Api.Authorization;
using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Artifact sub-resource routes on <c>/api/issues/{id}/artifacts</c>. Thin HTTP adapters over
/// <see cref="IArtifactService"/> — the same service the CLI/MCP agent surface calls directly, so
/// list ordering and error semantics cannot diverge between surfaces (AC-ART-103). Artifacts have
/// no permission of their own: they inherit the parent issue's (BR-ART-5).
///
/// The acting member is always read from the authenticated <c>ActorContext</c> rather than from the
/// request body or query string: an actor identity a caller can choose is an actor identity a
/// caller can forge, which would make <c>AddedById</c> and the audit trail untrustworthy.
/// </summary>
public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/issues").WithTags("Issues").RequirePermission(Permission.ReadBoard);

        group.MapGet("/{id:guid}/artifacts", async (
            HttpContext http, Guid id, IArtifactService service, CancellationToken ct) =>
        {
            try
            {
                // An issue with no artifacts is a 200 with an empty list; an issue that does not
                // exist, or that belongs to another workspace, is a 404.
                var artifacts = await service.ListArtifactsAsync(
                    new IssueId(id), http.GetActorContext().WorkspaceId, ct);
                return Results.Ok(artifacts);
            }
            catch (ArtifactException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCode(ex));
            }
        });

        group.MapPost("/{id:guid}/artifacts", async (
            HttpContext http, Guid id, AttachArtifactRequest request, IArtifactService service, CancellationToken ct) =>
        {
            try
            {
                var artifact = await service.AttachArtifactAsync(
                    new IssueId(id),
                    request.Kind,
                    request.Title,
                    request.ContentReference,
                    request.Source,
                    http.GetActorContext().MemberId,
                    request.Metadata,
                    AuditChannel.Rest,
                    http.GetActorContext().WorkspaceId,
                    ct);
                return Results.Created($"/api/issues/{id}/artifacts/{artifact.Id}", artifact);
            }
            catch (ArtifactException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCode(ex));
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapDelete("/{id:guid}/artifacts/{artifactId:guid}", async (
            HttpContext http, Guid id, Guid artifactId, IArtifactService service, CancellationToken ct) =>
        {
            try
            {
                await service.RemoveArtifactAsync(
                    new IssueId(id),
                    new ArtifactId(artifactId),
                    http.GetActorContext().MemberId,
                    AuditChannel.Rest,
                    http.GetActorContext().WorkspaceId,
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
/// <remarks>
/// Carries no actor field on purpose: provenance is taken from the authenticated
/// <see cref="Anvilboard.Application.Authorization.ActorContext"/>, never from the request, so a
/// caller cannot attribute an artifact to another member (BR-ART-2).
/// </remarks>
public sealed record AttachArtifactRequest(
    string Kind,
    string Title,
    string ContentReference,
    string? Source = null,
    string? Metadata = null);
