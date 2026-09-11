namespace Anvilboard.Application.Backup;

/// <summary>Identifies a previously created backup artifact by id (`docs/plans/backup-and-restore.md` §8.2).</summary>
public sealed record BackupArtifactRef(Guid BackupId);
