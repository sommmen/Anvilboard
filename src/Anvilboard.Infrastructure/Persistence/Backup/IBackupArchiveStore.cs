namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// Owns the on-disk backup directory layout under the configured backup directory
/// (`docs/plans/backup-and-restore.md` §10.3): one directory per backup containing a SQLite
/// snapshot and a manifest that is written last, so a partially written backup directory is never
/// mistaken for a complete one (plan §7.4, §8.3). This store only allocates paths and
/// (de)serializes manifests; it never opens the live database or a snapshot as SQLite — that is
/// <see cref="ISnapshotArchiver"/>'s responsibility. Every member throws on unexpected I/O failure;
/// <see cref="EnsureWritableAsync"/> and <see cref="EnsureFreeSpaceAsync"/> throw the more specific
/// <see cref="BackupStoreUnavailableException"/> for the two documented store-unavailable causes.
/// </summary>
public interface IBackupArchiveStore
{
    /// <summary>Ensures the configured backup directory exists and is writable, creating it on
    /// first use. Throws <see cref="BackupStoreUnavailableException"/> if it cannot be created or
    /// written to.</summary>
    Task EnsureWritableAsync(CancellationToken ct = default);

    /// <summary>Verifies the backup directory's volume has free space of at least twice
    /// <paramref name="currentDatabaseSizeBytes"/> (plan §7.4 "Insufficient disk space"). Throws
    /// <see cref="BackupStoreUnavailableException"/> naming the shortfall otherwise.</summary>
    Task EnsureFreeSpaceAsync(long currentDatabaseSizeBytes, CancellationToken ct = default);

    /// <summary>
    /// Allocates a new, empty staging directory for a backup in progress and returns the paths
    /// within it. The directory is not a complete backup — and is invisible to
    /// <see cref="ListBackupsAsync"/> and <see cref="FindBackup"/> — until
    /// <see cref="WriteManifestAsync"/> completes.
    /// </summary>
    BackupLocation CreateStagingLocation();

    /// <summary>Serializes and writes the manifest last, which is what marks a backup directory
    /// complete.</summary>
    Task WriteManifestAsync(BackupLocation location, BackupManifestData manifest, CancellationToken ct = default);

    /// <summary>Locates an existing backup directory by id, regardless of whether its manifest is
    /// complete. Returns <see langword="null"/> if no directory matches
    /// <paramref name="backupId"/>.</summary>
    BackupLocation? FindBackup(Guid backupId);

    /// <summary>Reads and deserializes the manifest for <paramref name="backupId"/>. Returns
    /// <see langword="null"/> if the backup directory, its manifest, or the manifest's content is
    /// missing or malformed (the caller maps this to <c>manifest_missing_or_malformed</c>).</summary>
    Task<BackupManifestData?> ReadManifestAsync(Guid backupId, CancellationToken ct = default);

    /// <summary>Lists only directories containing a complete, well-formed manifest, newest
    /// first.</summary>
    Task<IReadOnlyList<BackupManifestData>> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>Best-effort deletion of an incomplete staging directory after a failed backup.
    /// Never throws; returns <see langword="false"/> if deletion did not fully succeed, which the
    /// caller only needs to log (plan §8.3).</summary>
    bool TryDeleteIncompleteBackup(BackupLocation location);
}

/// <summary>
/// Paths for one backup's directory: <paramref name="DatabasePath"/> is the snapshot file and
/// <paramref name="ManifestPath"/> is <c>backup-manifest.json</c> within
/// <paramref name="DirectoryPath"/> (plan §10.3).
/// </summary>
public sealed record BackupLocation(Guid BackupId, string DirectoryPath, string DatabasePath, string ManifestPath);
