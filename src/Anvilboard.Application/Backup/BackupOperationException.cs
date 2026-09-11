namespace Anvilboard.Application.Backup;

/// <summary>
/// Stable application error thrown by <see cref="IBackupService"/> for the fail-closed
/// preconditions that stop a backup or restore before it can even be attempted
/// (`docs/plans/backup-and-restore.md` §7.6): an unresolvable workspace anchor, a malformed or
/// mismatched restore confirmation, an unknown backup id, a restore already in progress, or a
/// backup store that cannot be used. These are distinct from the five documented
/// <see cref="BackupFailedCheck"/> integrity-validation outcomes, which are reported as data on
/// <see cref="RestoreResult"/>/<see cref="BackupVerification"/> rather than thrown, because they
/// represent an examined-and-rejected artifact rather than a precondition that prevented the
/// operation from starting at all.
/// </summary>
public sealed class BackupOperationException(string errorCode, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public const string WorkspaceAccessDenied = "WORKSPACE_ACCESS_DENIED";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ReferencedEntityNotFound = "REFERENCED_ENTITY_NOT_FOUND";
    public const string RateLimited = "RATE_LIMITED";
    public const string ArtifactStoreUnavailable = "ARTIFACT_STORE_UNAVAILABLE";

    public string ErrorCode { get; } = errorCode;
}
