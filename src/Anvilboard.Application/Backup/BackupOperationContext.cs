using Anvilboard.Domain;

namespace Anvilboard.Application.Backup;

/// <summary>
/// Explicit actor, audit channel, and correlation id for a backup or restore operation
/// (`docs/plans/backup-and-restore.md` §8.2). Callers (REST, CLI, MCP) supply this rather than
/// letting backup orchestration infer the channel from runtime type or ambient process mode.
/// </summary>
public sealed record BackupOperationContext(string ActorId, AuditChannel Channel, string CorrelationId);
