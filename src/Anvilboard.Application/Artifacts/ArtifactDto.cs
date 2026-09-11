using Anvilboard.Domain;

namespace Anvilboard.Application.Artifacts;

/// <summary>
/// The transport-neutral shape of an artifact, returned identically by the REST endpoints and the
/// CLI/MCP agent surface so the two never diverge (AC-ART-103). <see cref="Kind"/> is the lower-snake
/// wire value produced by <see cref="ArtifactKindConverter"/>, never the enum member name.
/// </summary>
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
