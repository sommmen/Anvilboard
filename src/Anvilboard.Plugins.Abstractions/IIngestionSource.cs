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
}
