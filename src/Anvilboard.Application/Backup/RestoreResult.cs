namespace Anvilboard.Application.Backup;

/// <summary>
/// Outcome of a restore attempt (`docs/plans/backup-and-restore.md` §8.2, §8.4).
/// <see cref="FailedCheck"/> is one of the <see cref="BackupFailedCheck"/> values, or
/// <see langword="null"/> when <see cref="Success"/> is <see langword="true"/>.
/// </summary>
public sealed record RestoreResult(
    bool Success,
    Guid BackupId,
    DateTimeOffset RestoredAt,
    string? FailedCheck,
    string? Detail);
