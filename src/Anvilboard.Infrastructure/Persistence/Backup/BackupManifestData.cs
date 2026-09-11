using Anvilboard.Domain;

namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// On-disk shape of <c>backup-manifest.json</c> (`docs/plans/backup-and-restore.md` §8.2, §10.3).
/// Deliberately distinct from the application-facing manifest contract: this project
/// (<c>Anvilboard.Infrastructure</c>) is depended on by <c>Anvilboard.Application</c>, never the
/// other way around, so the REST/agent-facing <c>BackupManifest</c> record must live in
/// <c>Anvilboard.Application.Backup</c> and cannot be referenced from here. The application's
/// backup orchestration maps between the two.
/// </summary>
public sealed record BackupManifestData(
    Guid BackupId,
    WorkspaceId RequestedForWorkspaceId,
    string RequestedForWorkspaceSlug,
    IReadOnlyList<BackupWorkspaceEntry> Workspaces,
    DateTimeOffset CreatedAt,
    string ProductVersion,
    string SchemaVersion,
    string ChecksumSha256,
    long SizeBytes);

/// <summary>One workspace present in a backup's full blast radius (plan §5.1/§8.2).</summary>
public sealed record BackupWorkspaceEntry(WorkspaceId Id, string Slug);
