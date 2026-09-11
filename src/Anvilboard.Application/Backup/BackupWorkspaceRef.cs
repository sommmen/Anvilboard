using Anvilboard.Domain;

namespace Anvilboard.Application.Backup;

/// <summary>One workspace present in a backup's full blast radius (`docs/plans/backup-and-restore.md` §5.1/§8.2).</summary>
public sealed record BackupWorkspaceRef(WorkspaceId Id, string Slug);
