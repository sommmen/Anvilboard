namespace Anvilboard.Application.Backup;

/// <summary>
/// Result of verifying a backup's integrity without restoring it (`docs/plans/backup-and-restore.md`
/// §8.2, US-B6). <see cref="FailedCheck"/> is one of the <see cref="BackupFailedCheck"/> values, or
/// <see langword="null"/> when <see cref="Verified"/> is <see langword="true"/>.
/// </summary>
public sealed record BackupVerification(bool Verified, string? FailedCheck, string? Detail);
