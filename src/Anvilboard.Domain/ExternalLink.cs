namespace Anvilboard.Domain;

/// <summary>
/// Ties a local <see cref="Issue"/> to the record it was synced from in an external system. The
/// combination of (<see cref="Provider"/>, <see cref="SourceKey"/>) is the de-duplication key
/// every ingestion plugin must compute deterministically from the remote item (e.g.
/// "github:owner/repo#123" or "linear:ENG-42") so re-running a sync updates rather than
/// duplicates an issue.
/// </summary>
public sealed class ExternalLink
{
    public ExternalLinkId Id { get; init; }
    public required IssueId IssueId { get; init; }
    public required IntegrationProvider Provider { get; init; }

    /// <summary>Stable dedupe key within <see cref="Provider"/>; opaque to the core system.</summary>
    public required string SourceKey { get; init; }

    /// <summary>Deep link back to the item in its origin system, shown in the UI.</summary>
    public string? Url { get; set; }

    /// <summary>Raw last-synced hash/etag/updated-timestamp, used to skip no-op syncs cheaply.</summary>
    public string? SyncFingerprint { get; set; }

    public DateTimeOffset LastSyncedAt { get; set; }
}
