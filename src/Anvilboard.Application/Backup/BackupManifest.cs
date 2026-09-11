using Anvilboard.Domain;

namespace Anvilboard.Application.Backup;

/// <summary>
/// Snapshot metadata returned by create/list/verify and consumed by REST/agent callers
/// (`docs/plans/backup-and-restore.md` §8.2, §9.2). <see cref="Workspaces"/> is the full blast
/// radius a restore of this backup would overwrite — every workspace on the host at backup time,
/// not just <see cref="RequestedForWorkspaceId"/> (plan §5.1/§5.4): <see cref="RequestedForWorkspaceId"/>
/// and <see cref="RequestedForWorkspaceSlug"/> are the authorization/confirmation anchor the backup
/// was requested through, not a data filter.
/// </summary>
public sealed record BackupManifest(
    Guid BackupId,
    WorkspaceId RequestedForWorkspaceId,
    string RequestedForWorkspaceSlug,
    IReadOnlyList<BackupWorkspaceRef> Workspaces,
    DateTimeOffset CreatedAt,
    string ProductVersion,
    string SchemaVersion,
    string ChecksumSha256,
    long SizeBytes);
