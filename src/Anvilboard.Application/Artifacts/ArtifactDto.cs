using Anvilboard.Domain;

namespace Anvilboard.Application.Artifacts;

/// <summary>
/// The wire shape of an attached artifact. <see cref="Kind"/> is the lower-snake wire value rather
/// than the enum name, so the REST/CLI/MCP surfaces all emit the same vocabulary the
/// <c>Artifacts.Kind</c> column stores.
/// </summary>
/// <remarks>
/// Provenance is always distinguishable: manually attached artifacts carry <c>source = "local"</c>
/// with a non-null <see cref="AddedById"/>, while automation-attached ones carry the originating
/// hook/integration key and a null actor.
/// </remarks>
public sealed record ArtifactDto(
    Guid Id,
    Guid IssueId,
    string Kind,
    string Title,
    string ContentReference,
    string Source,
    Guid? AddedById,
    string? DedupKey,
    string? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ArtifactDto FromArtifact(Artifact artifact) => new(
        artifact.Id.Value,
        artifact.IssueId.Value,
        ArtifactKindConverter.ToWireValue(artifact.Kind),
        artifact.Title,
        artifact.ContentReference,
        artifact.Source,
        artifact.AddedById?.Value,
        artifact.DedupKey,
        artifact.Metadata,
        artifact.CreatedAt,
        artifact.UpdatedAt);
}

/// <summary>
/// Bytes the caller wants durably stored through the artifact store, rather than an
/// already-external locator. Supplying this makes the service — never the caller — responsible for
/// producing the resulting opaque <see cref="ArtifactDto.ContentReference"/>.
/// </summary>
public sealed record ArtifactInlineContent(byte[] Bytes, string? ContentType = null);
