using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Provider-agnostic shape an ingestion plugin maps a remote item into. The host's sync
/// coordinator upserts this into a local <see cref="Issue"/> keyed by
/// (<see cref="Provider"/>, <see cref="SourceKey"/>) — plugins never construct or mutate
/// <see cref="Issue"/> directly, which keeps every source (GitHub, Linear, a private Slack
/// plugin, ...) reduced to "produce these fields" instead of re-implementing dedupe/merge logic.
/// </summary>
/// <param name="SourceKey">
/// Deterministic dedupe key unique within <see cref="Provider"/>, e.g.
/// "owner/repo#123" for GitHub or "ENG-42" for Linear.
/// </param>
/// <param name="TeamKey">
/// Local team key (see <see cref="Team.Key"/>) the issue should be filed under; the plugin or its
/// configuration decides the mapping from remote project/repo to local team.
/// </param>
public sealed record NormalizedIssue(
    IntegrationProvider Provider,
    string SourceKey,
    string TeamKey,
    string Title,
    string? Description,
    IssueStatus SuggestedStatus,
    IssuePriority SuggestedPriority,
    string? Url,
    string? AssigneeEmail,
    IReadOnlyList<string> LabelNames,
    string? SyncFingerprint,
    DateTimeOffset RemoteUpdatedAt);

/// <summary>A comment observed on a remote item, normalized the same way as <see cref="NormalizedIssue"/>.</summary>
public sealed record NormalizedComment(
    string SourceKey,
    string Body,
    string? AuthorEmail,
    DateTimeOffset RemoteCreatedAt);
