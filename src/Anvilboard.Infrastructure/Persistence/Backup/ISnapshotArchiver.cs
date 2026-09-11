namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// Wraps the SQLite-specific mechanics for taking a consistent snapshot of the live database and,
/// for restore, validating and swapping a snapshot back into place
/// (`docs/plans/backup-and-restore.md` §7.1, §8.3-§8.4). Every member operates against the single
/// live database configured as <see cref="AnvilboardDbOptions.DatabasePath"/> for this host; there
/// is no per-workspace filtering (plan §5.1/§5.4) because the whole file is one snapshot unit.
/// </summary>
public interface ISnapshotArchiver
{
    /// <summary>
    /// Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c> against the live database so any pending WAL
    /// frames are folded into the main file and the WAL is truncated to zero bytes before a
    /// snapshot or safety copy is taken (plan §7.1 note).
    /// </summary>
    Task CheckpointAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes a consistent snapshot of the live database into <paramref name="destinationDatabasePath"/>
    /// using the SQLite Online Backup API (<c>SqliteConnection.BackupDatabase</c>), which is safe
    /// under concurrent writers and does not require exclusive access (plan §7.1).
    /// </summary>
    Task SnapshotAsync(string destinationDatabasePath, CancellationToken ct = default);

    /// <summary>Computes a streamed SHA-256 checksum of the file at <paramref name="databasePath"/>,
    /// returned as lowercase hex.</summary>
    Task<string> ComputeSha256Async(string databasePath, CancellationToken ct = default);

    /// <summary>Runs <c>PRAGMA integrity_check</c> against the database at
    /// <paramref name="databasePath"/>. Returns <see langword="true"/> only if SQLite reports a
    /// single "ok" row.</summary>
    Task<bool> VerifyIntegrityAsync(string databasePath, CancellationToken ct = default);

    /// <summary>
    /// Restore preflight (plan §8.4): attempts to take an exclusive SQLite lock on the live
    /// database with a zero busy timeout. Returns <see langword="false"/> (without throwing) if the
    /// database is busy or locked by another connection or process, which the caller maps to
    /// <c>ARTIFACT_STORE_UNAVAILABLE</c> so the live file is never touched. The lock is released
    /// before this method returns; it exists only to prove no other handle currently holds the
    /// file, not to hold it open across the swap.
    /// </summary>
    Task<bool> TryAcquireExclusiveAccessAsync(CancellationToken ct = default);

    /// <summary>
    /// Copies the live database file to <paramref name="safetyCopyPath"/> so a failed swap remains
    /// recoverable (plan §7.5 rule 3). Must run only after all restore validation has succeeded and
    /// before <see cref="SwapInAsync"/>. Checkpoints first so the copy is a self-contained,
    /// consistent single file.
    /// </summary>
    Task CreateSafetyCopyAsync(string safetyCopyPath, CancellationToken ct = default);

    /// <summary>
    /// Clears ADO.NET connection pools, then atomically replaces the live database file with the
    /// contents of <paramref name="stagedDatabasePath"/> (the file at <paramref name="stagedDatabasePath"/>
    /// itself is left untouched — it may be a backup artifact that must remain restorable again
    /// later), removing any stale <c>-wal</c>/<c>-shm</c>/<c>-journal</c> sidecar files so the
    /// restored file is not reopened against a foreign WAL (plan §8.4).
    /// </summary>
    Task SwapInAsync(string stagedDatabasePath, CancellationToken ct = default);
}
