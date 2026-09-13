namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// A pull-based plugin that periodically fetches work items from a remote system and yields them
/// as <see cref="NormalizedIssue"/>s for the host to upsert. This is the extension point GitHub
/// and Linear's first-class integrations implement, and the one a private "poll Jira" or
/// "poll a spreadsheet" plugin would implement too — nothing about it is GitHub/Linear specific.
/// The host's sync coordinator (<c>Anvilboard.Application</c>) calls <see cref="SyncAsync"/> on a
/// timer per <see cref="IngestionOptions.PollInterval"/> and persists whatever is yielded.
/// </summary>
public interface IIngestionSource : IAnvilboardPlugin
{
    /// <summary>
    /// Streams normalized issues observed since the last successful sync. Implementations should
    /// use <paramref name="cursor"/> (opaque, plugin-defined state persisted by the host between
    /// runs) to avoid re-fetching the entire remote history every poll.
    /// </summary>
    IAsyncEnumerable<NormalizedIssue> SyncAsync(SyncCursor cursor, CancellationToken cancellationToken);
}

/// <summary>
/// Opaque, plugin-owned continuation state (e.g. a "since" timestamp or GraphQL page cursor)
/// round-tripped by the host between sync runs so a plugin can do incremental fetches.
/// </summary>
public sealed class SyncCursor
{
    public static readonly SyncCursor Empty = new(null);

    public SyncCursor(string? token) => Token = token;

    public string? Token { get; }
}

/// <summary>Per-plugin polling configuration bound from <c>Plugins:&lt;Key&gt;</c> configuration.</summary>
public sealed class IngestionOptions
{
    public bool Enabled { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ceiling for exponential backoff after consecutive transient failures. Without a ceiling a
    /// long outage would push the next attempt days out, so a provider that recovered would stay
    /// un-synced long after it was healthy again.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The flat interval used after a non-transient failure (rejected credentials). Exponential
    /// backoff is the wrong shape here: retrying a revoked token never succeeds regardless of how
    /// long the wait is, so the goal is only to keep probing cheaply until it is reconfigured.
    /// </summary>
    public TimeSpan QuarantineInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an integration may go without a successful sync before it is reported as stale.
    /// Null means "derive from <see cref="PollInterval"/>", which is the default because a
    /// sensible threshold is a multiple of the cadence rather than an absolute duration.
    /// </summary>
    public TimeSpan? StalenessThreshold { get; set; }

    /// <summary>
    /// The effective staleness threshold: <see cref="StalenessThreshold"/> when configured,
    /// otherwise three poll intervals (floored at one minute) so a single missed poll — or one
    /// that merely overran — never trips a staleness alarm.
    /// </summary>
    public TimeSpan EffectiveStalenessThreshold =>
        StalenessThreshold is { } configured && configured > TimeSpan.Zero
            ? configured
            : Max(PollInterval * 3, TimeSpan.FromMinutes(1));

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}
