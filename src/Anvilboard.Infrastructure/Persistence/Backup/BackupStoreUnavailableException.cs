namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// Thrown when the configured backup directory cannot be used to create or read backups —
/// unwritable, unreadable, or without enough free space for a new snapshot
/// (`docs/plans/backup-and-restore.md` §7.4, §7.6). Callers translate this into the documented
/// <c>ARTIFACT_STORE_UNAVAILABLE</c> error rather than exposing a raw I/O exception, mirroring
/// <c>IArtifactStore</c>'s existing translation contract.
/// </summary>
public sealed class BackupStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
