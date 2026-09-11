using Anvilboard.Domain;

namespace Anvilboard.Application.Artifacts;

// `channel` is threaded explicitly rather than inferred from an ambient accessor because the same
// scoped service instance serves REST, CLI, MCP, and in-process hooks; only the caller knows which
// it is. This mirrors how `BackupOperationContext` supplies the same fact to `IBackupService`.

/// <summary>
/// The single write/read path for issue artifacts, used identically by the REST endpoints, the
/// CLI/MCP agent surface, and in-process lifecycle hooks — and the only caller of
/// <c>IArtifactStore</c> (docs/features/artifacts.md, "Persistence abstraction ownership"), so no
/// other component depends on a concrete storage mechanism.
/// </summary>
public interface IArtifactService
{
    /// <summary>
    /// Attaches an artifact whose content is already addressable by
    /// <paramref name="contentReference"/> (a link, a deployment URL, a pull request URL, or a
    /// reference a caller already obtained from the store). <paramref name="source"/> defaults to
    /// <c>"local"</c> only when <paramref name="actorId"/> is supplied; automation callers must pass
    /// their own hook key so automation provenance is never mistaken for a human action (BR-ART-1),
    /// and may not claim <c>"local"</c> explicitly either.
    /// </summary>
    /// <param name="workspaceScope">The authenticated caller's workspace. When supplied, an
    /// <paramref name="issueId"/> outside it is rejected as <c>REFERENCED_ENTITY_NOT_FOUND</c>;
    /// omit it only for unauthenticated in-process callers that have no workspace to assert.</param>
    Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        string contentReference,
        string? source = null,
        MemberId? actorId = null,
        string? metadata = null,
        AuditChannel channel = AuditChannel.System,
        WorkspaceId? workspaceScope = null,
        CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="content"/> through the active artifact store and attaches the
    /// resulting reference atomically: if the store throws, no row is created (AC-ART-104).
    /// </summary>
    /// <param name="workspaceScope">See the reference overload.</param>
    Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        byte[] content,
        string? contentType,
        string source,
        MemberId? actorId = null,
        string? metadata = null,
        AuditChannel channel = AuditChannel.System,
        WorkspaceId? workspaceScope = null,
        CancellationToken ct = default);

    /// <summary>All artifacts on the issue, <c>CreatedAt</c> ascending with <c>Id</c> as the
    /// tie-break so ordering is deterministic across every surface (AC-ART-103). An unknown or
    /// out-of-scope issue is <c>REFERENCED_ENTITY_NOT_FOUND</c>, never an empty list.</summary>
    /// <param name="workspaceScope">See <see cref="AttachArtifactAsync(IssueId, string, string, string, string?, MemberId?, string?, AuditChannel, WorkspaceId?, CancellationToken)"/>.</param>
    Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(
        IssueId issueId, WorkspaceId? workspaceScope = null, CancellationToken ct = default);

    /// <summary>
    /// Idempotent upsert on <c>(issueId, kind, dedupKey)</c>. Valid only for refreshable kinds
    /// (currently <c>pull_request</c>, BR-ART-3). Deliberately bound to no transport: pull request
    /// state must only ever reflect what the provider reports, so this is reachable from plugin
    /// correlation logic only and never from REST/CLI/MCP.
    /// </summary>
    Task<ArtifactDto> RefreshArtifactAsync(
        IssueId issueId,
        string kind,
        string dedupKey,
        string title,
        string contentReference,
        string source,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>Removes the artifact and purges its content per the active store's documented
    /// policy; always emits <c>ArtifactRemoved</c> (AC-ART-105).</summary>
    /// <param name="workspaceScope">See <see cref="AttachArtifactAsync(IssueId, string, string, string, string?, MemberId?, string?, AuditChannel, WorkspaceId?, CancellationToken)"/>.</param>
    Task RemoveArtifactAsync(
        IssueId issueId,
        ArtifactId artifactId,
        MemberId? actorId = null,
        AuditChannel channel = AuditChannel.System,
        WorkspaceId? workspaceScope = null,
        CancellationToken ct = default);
}
