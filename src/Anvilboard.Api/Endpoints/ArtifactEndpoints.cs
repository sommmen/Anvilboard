using Anvilboard.Api.Authorization;
using Anvilboard.Application.Artifacts;
using Anvilboard.Application.Automation;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Issue artifact endpoints. Thin HTTP adapters over <see cref="IArtifactService"/> — the same
/// service the CLI/MCP agent surface and approved lifecycle hooks call directly, so an artifact
/// attached by an automation is validated and audited exactly like one attached by a human.
/// </summary>
/// <remarks>
/// Attaching or removing an artifact requires the same permission as mutating the issue itself
/// (<see cref="Permission.ReadWriteIssues"/> or <see cref="Permission.ReadWriteAssignedIssues"/>);
/// there is no separate artifact-admin role. Refresh is deliberately absent: it is an upsert
/// reachable only from the owning plugin's correlation logic, never from a public write path.
/// </remarks>
public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/issues")
            .WithTags("Artifacts")
            .RequirePermission(Permission.ReadBoard);

        group.MapGet("/{id:guid}/artifacts", async (Guid id, IArtifactService service, RestWorkspaceScope scope, CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var artifacts = await service.ListArtifactsAsync(scope.WorkspaceId, issueId, ct);
                return Results.Ok(artifacts);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (ArtifactException ex)
            {
                return Problem(ex);
            }
        });

        group.MapPost("/{id:guid}/artifacts", async (
            Guid id,
            AttachArtifactRequest request,
            IArtifactService service,
            RestWorkspaceScope scope,
            CancellationToken ct) =>
        {
            if (!ArtifactKindConverter.TryParse(request.Kind, out var kind))
            {
                return Results.Problem(
                    title: "VALIDATION_FAILED",
                    detail: $"'{request.Kind}' is not a supported artifact kind.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!TryDecodeContent(request, out var inlineContent))
            {
                return Results.Problem(
                    title: "VALIDATION_FAILED",
                    detail: "Artifact 'contentBase64' must be valid base64 content.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var actor = await scope.RequireMemberAsync(request.ActorId, ct);
                var artifact = await service.AttachArtifactAsync(
                    scope.WorkspaceId,
                    issueId,
                    kind,
                    request.Title,
                    request.ContentReference,
                    inlineContent,
                    request.Source,
                    actor,
                    request.DedupKey,
                    request.Metadata,
                    ct);

                return Results.Created($"/api/issues/{id}/artifacts/{artifact.Id}", artifact);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (ArtifactException ex)
            {
                return Problem(ex);
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);

        group.MapDelete("/{id:guid}/artifacts/{artifactId:guid}", async (
            Guid id,
            Guid artifactId,
            Guid? actorId,
            IArtifactService service,
            RestWorkspaceScope scope,
            CancellationToken ct) =>
        {
            try
            {
                var issueId = await scope.RequireIssueAsync(id, ct);
                var actor = await scope.RequireMemberAsync(actorId, ct);
                await service.RemoveArtifactAsync(
                    scope.WorkspaceId,
                    issueId,
                    new ArtifactId(artifactId),
                    actor,
                    ct);
                return Results.NoContent();
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (ArtifactException ex)
            {
                return Problem(ex);
            }
        }).RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues);
    }

    /// <summary>
    /// Maps through the shared error catalog rather than a local switch, so this surface can never
    /// drift from the status codes the CLI/MCP surface reports for the same condition.
    /// </summary>
    private static IResult Problem(ArtifactException ex) =>
        Results.Problem(
            title: ex.ErrorCode,
            detail: ex.Message,
            statusCode: ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode));

    private static bool TryDecodeContent(AttachArtifactRequest request, out ArtifactInlineContent? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(request.ContentBase64))
        {
            return true;
        }

        var buffer = new byte[request.ContentBase64.Length];
        if (!Convert.TryFromBase64String(request.ContentBase64, buffer, out var bytesWritten))
        {
            return false;
        }

        content = new ArtifactInlineContent(buffer[..bytesWritten], request.ContentType);
        return true;
    }
}

/// <param name="Kind">The wire kind: <c>file</c>, <c>link</c>, <c>deployment</c>, or <c>pull_request</c>.</param>
/// <param name="ContentReference">An external locator; mutually exclusive with <paramref name="ContentBase64"/>.</param>
/// <param name="ContentBase64">Inline bytes to store durably, for artifacts whose content Anvilboard owns.</param>
/// <param name="Source">The originating integration key; defaults to <c>local</c> when omitted.</param>
public sealed record AttachArtifactRequest(
    string Kind,
    string Title,
    string? ContentReference = null,
    string? ContentBase64 = null,
    string? ContentType = null,
    string? Source = null,
    Guid? ActorId = null,
    string? DedupKey = null,
    string? Metadata = null);
