using Anvilboard.Domain;

namespace Anvilboard.Application.Backup;

/// <summary>
/// Snapshot and recovery for the single SQLite file that holds the whole instance
/// (`docs/plans/backup-and-restore.md` §8.1-§8.4). The <see cref="WorkspaceId"/> on every member
/// is the authorization and confirmation anchor, not a data filter: a snapshot always contains
/// every workspace on the host (plan §5.1/§5.4). Callers (REST, CLI, MCP) supply an explicit
/// <see cref="BackupOperationContext"/> rather than letting this service infer the actor, audit
/// channel, or correlation id from runtime type or ambient process mode.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Creates a verified snapshot of the live database (plan §8.3). Throws
    /// <see cref="BackupOperationException"/> (<see cref="BackupOperationException.WorkspaceAccessDenied"/>
    /// or <see cref="BackupOperationException.ArtifactStoreUnavailable"/>) if a precondition fails
    /// before any artifact is produced.
    /// </summary>
    Task<BackupManifest> CreateBackupAsync(
        WorkspaceId workspaceId, BackupOperationContext operation, CancellationToken ct = default);

    /// <summary>
    /// Restores the live database from a previously created backup (plan §8.4), fail-closed:
    /// authorization anchor, then confirmation match, then restore exclusivity, then artifact
    /// existence, are all verified before any artifact or the live database is touched, and throw
    /// <see cref="BackupOperationException"/> on failure. The five documented integrity causes
    /// (checksum, SQLite integrity, manifest, schema version, workspace set) instead produce a
    /// <see cref="RestoreResult"/> with <see cref="RestoreResult.Success"/> <see langword="false"/>
    /// and a populated <see cref="RestoreResult.FailedCheck"/>, because they represent an
    /// examined-and-rejected artifact rather than a precondition failure.
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, string confirmedWorkspaceSlug,
        BackupOperationContext operation, CancellationToken ct = default);

    /// <summary>Lists available, complete backups, newest first.</summary>
    Task<IReadOnlyList<BackupManifest>> ListBackupsAsync(
        WorkspaceId workspaceId, CancellationToken ct = default);

    /// <summary>Verifies a backup's integrity without restoring it (plan §8.2, US-B6).</summary>
    Task<BackupVerification> VerifyBackupAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, CancellationToken ct = default);
}
