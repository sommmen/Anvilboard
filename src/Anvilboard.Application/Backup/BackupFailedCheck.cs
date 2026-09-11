namespace Anvilboard.Application.Backup;

/// <summary>
/// Stable, machine-readable values for <see cref="RestoreResult.FailedCheck"/> and
/// <see cref="BackupVerification.FailedCheck"/> (`docs/plans/backup-and-restore.md` §8.2), so an
/// agent caller (US-B6) can branch on the cause without parsing prose. Centralized here so every
/// producer and consumer agrees on the exact string.
/// </summary>
public static class BackupFailedCheck
{
    public const string ChecksumMismatch = "checksum_mismatch";
    public const string SqliteIntegrityCheckFailed = "sqlite_integrity_check_failed";
    public const string ManifestMissingOrMalformed = "manifest_missing_or_malformed";
    public const string SchemaVersionIncompatible = "schema_version_incompatible";
    public const string WorkspaceSetMismatch = "workspace_set_mismatch";
}
