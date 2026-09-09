namespace Anvilboard.Infrastructure.Artifacts;

/// <summary>
/// The single persistence-abstraction contract for artifact content (`docs/features/artifacts.md`
/// §"Persistence abstraction ownership"). <c>Anvilboard.Application</c>'s <c>ArtifactService</c> is
/// the only caller; it never depends on a concrete storage mechanism, so the first-release SQLite
/// BLOB-backed <see cref="SqliteArtifactStore"/> can later be swapped for a filesystem- or
/// object-storage-backed implementation without changing any caller.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Durably stores <paramref name="content"/> and returns the opaque reference that later
    /// identifies it for <see cref="RetrieveAsync"/>/<see cref="DeleteAsync"/>. Throws on any
    /// read/write failure (unreachable/unavailable store) — callers translate this into the
    /// documented <c>ARTIFACT_STORE_UNAVAILABLE</c> error rather than exposing a raw I/O exception.
    /// </summary>
    Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default);

    /// <summary>Retrieves previously stored content by its opaque reference, or <see langword="null"/>
    /// if no content is stored under that reference.</summary>
    Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default);

    /// <summary>Deletes previously stored content by its opaque reference. A no-op (not an error) if
    /// nothing is stored under that reference.</summary>
    Task DeleteAsync(string reference, CancellationToken ct = default);
}

/// <summary>Content retrieved from an <see cref="IArtifactStore"/>.</summary>
public sealed record ArtifactContent(byte[] Bytes, string? ContentType);
