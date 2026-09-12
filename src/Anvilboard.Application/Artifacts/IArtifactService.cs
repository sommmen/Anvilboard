using Anvilboard.Domain;

namespace Anvilboard.Application.Artifacts;

/// <summary>
/// The single mutation and read path for issue artifacts (`docs/features/artifacts.md`). Every
/// caller — REST, CLI, MCP, and approved lifecycle hooks — goes through these methods, so there is
/// no privileged automation path: hook-attached artifacts differ from human-attached ones only in
/// their recorded provenance, never in the validation or auditing they receive.
/// </summary>
/// <remarks>
/// Implementations assume the request is already authorized by the host (the REST group's
/// permission requirement, or the hook's capability grant) and enforce only workspace reachability
/// of the target issue. All anticipated failures surface as
/// <see cref="ArtifactException"/> carrying a catalogued error code.
/// </remarks>
public interface IArtifactService
{
    /// <summary>
    /// Attaches a new artifact to <paramref name="issueId"/>.
    /// </summary>
    /// <param name="inlineContent">
    /// When supplied, the bytes are written through the artifact store first and the resulting
    /// opaque reference becomes the artifact's content reference; otherwise
    /// <paramref name="contentReference"/> is used verbatim. Exactly one of the two is required.
    /// </param>
    /// <param name="source">
    /// The originating integration/hook key, or <see langword="null"/> to default to
    /// <c>"local"</c> for manual attachment.
    /// </param>
    /// <exception cref="ArtifactException">
    /// <c>REFERENCED_ENTITY_NOT_FOUND</c> when the issue is unknown or outside the workspace;
    /// <c>VALIDATION_FAILED</c> naming the offending field; <c>ARTIFACT_STORE_UNAVAILABLE</c> when
    /// inline content could not be stored — in which case no artifact row is persisted.
    /// </exception>
    Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        ArtifactKind kind,
        string title,
        string? contentReference = null,
        ArtifactInlineContent? inlineContent = null,
        string? source = null,
        MemberId? actorId = null,
        string? dedupKey = null,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists every artifact attached to <paramref name="issueId"/>, oldest first.
    /// </summary>
    /// <exception cref="ArtifactException">
    /// <c>REFERENCED_ENTITY_NOT_FOUND</c> when the issue is unknown or outside the workspace.
    /// </exception>
    Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(IssueId issueId, CancellationToken ct = default);

    /// <summary>
    /// Idempotently upserts a refreshable artifact identified by
    /// <paramref name="dedupKey"/>. When a matching artifact already exists its content reference,
    /// title, and metadata are updated in place — preserving the original identity, attributed
    /// actor, and creation timestamp — otherwise a new artifact is attached, so the first provider
    /// event succeeds without a separate create call.
    /// </summary>
    /// <remarks>
    /// Reachable only from the owning plugin's correlation logic; it is deliberately not exposed as
    /// a public REST write path.
    /// </remarks>
    /// <exception cref="ArtifactException">
    /// <c>REFERENCED_ENTITY_NOT_FOUND</c> when the issue is unknown or outside the workspace;
    /// <c>VALIDATION_FAILED</c> when the kind is not refreshable or a field is invalid.
    /// </exception>
    Task<ArtifactDto> RefreshArtifactAsync(
        IssueId issueId,
        ArtifactKind kind,
        string dedupKey,
        string title,
        string contentReference,
        string? metadata = null,
        string source = "github",
        CancellationToken ct = default);

    /// <summary>
    /// Removes an artifact from <paramref name="issueId"/> and purges any content it owns.
    /// </summary>
    /// <exception cref="ArtifactException">
    /// <c>REFERENCED_ENTITY_NOT_FOUND</c> when the artifact does not exist or is attached to a
    /// different issue.
    /// </exception>
    Task RemoveArtifactAsync(
        IssueId issueId,
        ArtifactId artifactId,
        MemberId? actorId = null,
        CancellationToken ct = default);
}
