namespace Anvilboard.Infrastructure.Persistence;

/// <summary>Bound from the "Database" configuration section.</summary>
public sealed class AnvilboardDbOptions
{
    /// <summary>
    /// Filesystem path to the SQLite database file. Defaults to a file next to the running
    /// executable so a self-contained publish + this one file is the entire deployable app.
    /// </summary>
    public string DatabasePath { get; set; } = "anvilboard.db";

    /// <summary>
    /// Directory holding backup snapshots (see `docs/plans/backup-and-restore.md` §10.2). When
    /// unset, <see cref="ResolveBackupDirectory"/> defaults to a "backups" folder next to
    /// <see cref="DatabasePath"/> so a published single-file deployment needs no extra config.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>
    /// Resolves the effective backup directory: <see cref="BackupDirectory"/> if configured,
    /// otherwise a "backups" folder next to <see cref="DatabasePath"/>. Matches the resolution
    /// rule in `docs/plans/backup-and-restore.md` §10.2 exactly.
    /// </summary>
    public string ResolveBackupDirectory() =>
        BackupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath)) ?? ".", "backups");
}
