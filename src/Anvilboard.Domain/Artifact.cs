namespace Anvilboard.Domain;

/// <summary>
/// A file, link, deployment, or pull-request reference attached to an issue. Content is never
/// inlined here — <see cref="ContentReference"/> is an opaque locator resolved by whichever
/// <c>IArtifactStore</c> implementation is active, so this entity never depends on a concrete
/// storage mechanism.
/// </summary>
public sealed class Artifact
{
    public ArtifactId Id { get; init; }
    public required IssueId IssueId { get; init; }
    public required ArtifactKind Kind { get; init; }
    public required string Title { get; set; }
    public required string ContentReference { get; set; }

    /// <summary>"local" for manually-attached artifacts, or the originating integration/hook key
    /// (e.g. "slack-thread-expansion", "github") for automation-attached ones.</summary>
    public required string Source { get; init; }

    /// <summary>The attaching member, or null when the artifact was attached by an automation/hook
    /// rather than a human actor.</summary>
    public MemberId? AddedById { get; init; }

    /// <summary>Opaque provider identity (e.g. "github:{repo}#{number}") used only by refreshable
    /// kinds for upsert-in-place; unique per <see cref="IssueId"/> when set.</summary>
    public string? DedupKey { get; init; }

    /// <summary>Opaque JSON key-value bag populated only for refreshable kinds (currently
    /// <see cref="ArtifactKind.PullRequest"/>: number/state/checksStatus); never inspected by
    /// <c>IArtifactStore</c>.</summary>
    public string? Metadata { get; set; }

    /// <summary>Set once when the artifact is attached; refreshing an existing artifact preserves
    /// it, so the original attachment time is never lost to a provider update.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The small, closed set of artifact shapes this component understands. This is not the
/// workspace-configurable free-form taxonomy used for <c>Issue.Type</c>/<c>Priority</c>.
/// </summary>
public enum ArtifactKind
{
    File,
    Link,
    Deployment,
    PullRequest,
}

/// <summary>
/// Converts between <see cref="ArtifactKind"/> and the lower-snake wire/storage values documented
/// in the `Artifacts.Kind` column (tech-design §10.1): <c>file</c>, <c>link</c>, <c>deployment</c>,
/// <c>pull_request</c>.
/// </summary>
public static class ArtifactKindConverter
{
    public static string ToWireValue(ArtifactKind kind) => kind switch
    {
        ArtifactKind.File => "file",
        ArtifactKind.Link => "link",
        ArtifactKind.Deployment => "deployment",
        ArtifactKind.PullRequest => "pull_request",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParse(string? value, out ArtifactKind kind)
    {
        switch (value?.Trim())
        {
            case "file": kind = ArtifactKind.File; return true;
            case "link": kind = ArtifactKind.Link; return true;
            case "deployment": kind = ArtifactKind.Deployment; return true;
            case "pull_request": kind = ArtifactKind.PullRequest; return true;
            default:
                kind = default;
                return false;
        }
    }

    public static ArtifactKind Parse(string value)
    {
        if (TryParse(value, out var kind))
        {
            return kind;
        }

        throw new InvalidOperationException($"'{value}' is not a valid artifact kind.");
    }
}

/// <summary>
/// The BLOB backing a <c>file</c>-kind <see cref="Artifact.ContentReference"/> when stored by the
/// first-release <c>SqliteArtifactStore</c>. Deliberately not linked to <see cref="Artifact"/> by
/// foreign key: the reference is opaque from the store's point of view, and reuse (or absence) of
/// this row is entirely the store implementation's concern, not the domain's.
/// </summary>
public sealed class ArtifactBlob
{
    public required string Reference { get; init; }
    public required byte[] Bytes { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
