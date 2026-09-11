using System.Diagnostics;
using System.Reflection;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InfraBackupManifestData = Anvilboard.Infrastructure.Persistence.Backup.BackupManifestData;

namespace Anvilboard.Application.Backup;

/// <summary>
/// Orchestrates whole-database backup and restore (`docs/plans/backup-and-restore.md` §8.1-§8.4)
/// by composing <see cref="IBackupArchiveStore"/> (directory/manifest bookkeeping) and
/// <see cref="ISnapshotArchiver"/> (SQLite-specific snapshot/verify/swap mechanics). Restore is
/// fail-closed: every validation check (checksum, integrity, schema version, workspace set) runs
/// against the artifact before the live database is touched, and only a validated restore closes
/// admission, drains active work, and swaps the file.
/// </summary>
public sealed class BackupService(
    AnvilboardDbContext db,
    IBackupArchiveStore store,
    ISnapshotArchiver archiver,
    IRestoreCoordinator coordinator,
    IServiceScopeFactory scopeFactory,
    IOptions<AnvilboardDbOptions> dbOptions,
    IAuditService audit,
    ILogger<BackupService> logger) : IBackupService
{
    /// <summary>
    /// <see cref="RestoreResult.FailedCheck"/> value for the one failure mode this batch handles
    /// as data rather than as one of the five artifact-integrity causes in
    /// <see cref="BackupFailedCheck"/>: a post-swap migration or audit-write failure. Deliberately
    /// not added to the shared <see cref="BackupFailedCheck"/> catalog (a Batch 1 contract file),
    /// since it is not one of the plan's five documented pre-swap integrity checks — it can only
    /// happen after every one of those checks already passed.
    /// </summary>
    private const string PostSwapFailure = "post_swap_failure";

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public async Task<BackupManifest> CreateBackupAsync(
        WorkspaceId workspaceId, BackupOperationContext operation, CancellationToken ct = default)
    {
        var workspace = await ResolveWorkspaceAnchorAsync(workspaceId, ct);

        // Steps before this point (anchor resolution) never touch the backup store and are never
        // audited, mirroring the same no-audit-before-artifact-exists rule as restore's early gates.
        // No BackupId exists yet either, so a failure here is neither audited nor cleaned up via
        // TryDeleteIncompleteBackup -- it is translated to the documented ArtifactStoreUnavailable
        // precondition failure and thrown immediately.
        try
        {
            await store.EnsureWritableAsync(ct);
            var liveSizeBytes = new FileInfo(dbOptions.Value.DatabasePath) is { Exists: true } fileInfo ? fileInfo.Length : 0L;
            await store.EnsureFreeSpaceAsync(liveSizeBytes, ct);
        }
        catch (BackupStoreUnavailableException ex)
        {
            throw new BackupOperationException(BackupOperationException.ArtifactStoreUnavailable, ex.Message, ex);
        }

        var stopwatch = Stopwatch.StartNew();
        var location = store.CreateStagingLocation();
        try
        {
            await archiver.CheckpointAsync(ct);
            await archiver.SnapshotAsync(location.DatabasePath, ct);
            var checksum = await archiver.ComputeSha256Async(location.DatabasePath, ct);
            var sizeBytes = new FileInfo(location.DatabasePath).Length;

            var schemaVersion = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "none";
            var workspaces = await db.Workspaces
                .Select(w => new BackupWorkspaceEntry(w.Id, w.Slug))
                .ToListAsync(ct);

            var manifestData = new InfraBackupManifestData(
                location.BackupId,
                workspaceId,
                workspace.Slug,
                workspaces,
                DateTimeOffset.UtcNow,
                ResolveProductVersion(),
                schemaVersion,
                checksum,
                sizeBytes);

            await store.WriteManifestAsync(location, manifestData, ct);
            stopwatch.Stop();

            logger.LogInformation(
                "Created backup {BackupId} for workspace {WorkspaceId}: {SizeBytes} bytes, schema {SchemaVersion}, in {DurationMs}ms.",
                location.BackupId, workspaceId, sizeBytes, schemaVersion, stopwatch.ElapsedMilliseconds);

            await audit.RecordAsync(new AuditEventRequest(
                workspaceId,
                operation.ActorId,
                operation.Channel,
                "workspace.backup.created",
                "backup",
                location.BackupId.ToString(),
                operation.CorrelationId,
                $"schemaVersion={schemaVersion};sizeBytes={sizeBytes};checksumPrefix={checksum[..12]}"), ct);

            return ToApplicationManifest(manifestData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            store.TryDeleteIncompleteBackup(location);

            logger.LogWarning(ex, "Backup {BackupId} for workspace {WorkspaceId} failed.", location.BackupId, workspaceId);

            await audit.RecordAsync(new AuditEventRequest(
                workspaceId,
                operation.ActorId,
                operation.Channel,
                "workspace.backup.failed",
                "backup",
                location.BackupId.ToString(),
                operation.CorrelationId,
                $"failureCategory={ex.GetType().Name}"), ct);

            if (ex is BackupStoreUnavailableException)
            {
                throw new BackupOperationException(BackupOperationException.ArtifactStoreUnavailable, ex.Message, ex);
            }

            throw;
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        WorkspaceId workspaceId,
        BackupArtifactRef artifact,
        string confirmedWorkspaceSlug,
        BackupOperationContext operation,
        CancellationToken ct = default)
    {
        // 1: authorization anchor — no I/O, no audit (mirrors CreateBackupAsync and the
        // AuthenticateAsync convention of never disclosing whether a workspace exists).
        var workspace = await ResolveWorkspaceAnchorAsync(workspaceId, ct);

        // 2: explicit target confirmation (plan §11.2 "defense in depth") — no I/O, no audit.
        ValidateConfirmedSlug(confirmedWorkspaceSlug, workspace.Slug);

        // 3: restore exclusivity. From here on, every exit path is audited, since a backupId now
        // anchors the audit target even where the artifact turns out to be unusable.
        using var restoreLease = coordinator.TryBeginRestore()
            ?? throw await DenyRestoreAsync(
                workspaceId, artifact, operation, BackupOperationException.RateLimited,
                "A restore is already in progress.", ct);

        // 4: artifact existence.
        var location = store.FindBackup(artifact.BackupId)
            ?? throw await DenyRestoreAsync(
                workspaceId, artifact, operation, BackupOperationException.ReferencedEntityNotFound,
                $"No backup with id '{artifact.BackupId}' exists.", ct);

        // 5: manifest well-formed.
        var manifestData = await store.ReadManifestAsync(artifact.BackupId, ct);
        if (manifestData is null)
        {
            return await RejectAsync(
                workspaceId, artifact, operation, BackupFailedCheck.ManifestMissingOrMalformed,
                "The backup's manifest is missing or malformed.", ct);
        }

        // 6: checksum.
        var actualChecksum = await archiver.ComputeSha256Async(location.DatabasePath, ct);
        if (!string.Equals(actualChecksum, manifestData.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return await RejectAsync(
                workspaceId, artifact, operation, BackupFailedCheck.ChecksumMismatch,
                "The artifact's checksum does not match the recorded manifest checksum.", ct);
        }

        // 7: SQLite-level integrity.
        if (!await archiver.VerifyIntegrityAsync(location.DatabasePath, ct))
        {
            return await RejectAsync(
                workspaceId, artifact, operation, BackupFailedCheck.SqliteIntegrityCheckFailed,
                "PRAGMA integrity_check failed against the artifact.", ct);
        }

        // 8: schema-version compatibility — the artifact's recorded schema version must be one
        // this build already knows about (i.e. no newer than the running build).
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (!appliedMigrations.Contains(manifestData.SchemaVersion))
        {
            return await RejectAsync(
                workspaceId, artifact, operation, BackupFailedCheck.SchemaVersionIncompatible,
                $"Artifact schema version '{manifestData.SchemaVersion}' is not known to this build.", ct);
        }

        // 9: workspace-set mismatch — every live workspace must survive the restore. Fails closed
        // with no override (an earlier acknowledge-workspace-loss override was rejected in
        // planning).
        var manifestWorkspaceIds = manifestData.Workspaces.Select(w => w.Id).ToHashSet();
        var liveWorkspaceIds = await db.Workspaces.Select(w => w.Id).ToListAsync(ct);
        if (liveWorkspaceIds.Any(id => !manifestWorkspaceIds.Contains(id)))
        {
            return await RejectAsync(
                workspaceId, artifact, operation, BackupFailedCheck.WorkspaceSetMismatch,
                "The artifact does not contain every workspace currently on this host.", ct);
        }

        // Every fail-closed check above passed without touching the live database (AC-012).
        try
        {
            await coordinator.CloseAdmissionAndDrainAsync(restoreLease, DrainTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            // CloseAdmissionAndDrainAsync closes admission before polling, so a timed-out drain
            // still leaves admission closed; reopen it before reporting the failure.
            coordinator.ReopenAdmission(restoreLease);
            return await RejectAsync(
                workspaceId, artifact, operation, null, ex.Message, ct,
                errorCode: BackupOperationException.ArtifactStoreUnavailable);
        }

        try
        {
            if (!await archiver.TryAcquireExclusiveAccessAsync(ct))
            {
                return await RejectAsync(
                    workspaceId, artifact, operation, null,
                    "The live database is busy or locked by another connection or process.", ct,
                    errorCode: BackupOperationException.ArtifactStoreUnavailable);
            }

            var safetyCopyPath = BuildSafetyCopyPath();
            await archiver.CreateSafetyCopyAsync(safetyCopyPath, ct);
            await archiver.SwapInAsync(location.DatabasePath, ct);

            try
            {
                var (rollForwardApplied, restoredAt) = await MigrateAndAuditSuccessAsync(
                    workspaceId, artifact, manifestData, operation, ct);

                logger.LogInformation(
                    "Restored workspace {WorkspaceId} from backup {BackupId} (rollForwardApplied={RollForwardApplied}).",
                    workspaceId, artifact.BackupId, rollForwardApplied);

                return new RestoreResult(true, artifact.BackupId, restoredAt, null, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Post-swap step failed for backup {BackupId}; rolling back to safety copy.", artifact.BackupId);

                await archiver.SwapInAsync(safetyCopyPath, ct);
                await RecordRestoreFailureInRecoveredDbAsync(workspaceId, artifact, operation, ex.Message, ct);

                return new RestoreResult(false, artifact.BackupId, DateTimeOffset.UtcNow, PostSwapFailure, ex.Message);
            }
        }
        finally
        {
            coordinator.ReopenAdmission(restoreLease);
        }
    }

    public async Task<IReadOnlyList<BackupManifest>> ListBackupsAsync(WorkspaceId workspaceId, CancellationToken ct = default)
    {
        await ResolveWorkspaceAnchorAsync(workspaceId, ct);

        var manifests = await store.ListBackupsAsync(ct);
        return manifests.Select(ToApplicationManifest).ToList();
    }

    public async Task<BackupVerification> VerifyBackupAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, CancellationToken ct = default)
    {
        await ResolveWorkspaceAnchorAsync(workspaceId, ct);

        var location = store.FindBackup(artifact.BackupId)
            ?? throw new BackupOperationException(
                BackupOperationException.ReferencedEntityNotFound, $"No backup with id '{artifact.BackupId}' exists.");

        var manifestData = await store.ReadManifestAsync(artifact.BackupId, ct);
        if (manifestData is null)
        {
            return new BackupVerification(false, BackupFailedCheck.ManifestMissingOrMalformed, "The backup's manifest is missing or malformed.");
        }

        var actualChecksum = await archiver.ComputeSha256Async(location.DatabasePath, ct);
        if (!string.Equals(actualChecksum, manifestData.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new BackupVerification(false, BackupFailedCheck.ChecksumMismatch, "The artifact's checksum does not match the recorded manifest checksum.");
        }

        if (!await archiver.VerifyIntegrityAsync(location.DatabasePath, ct))
        {
            return new BackupVerification(false, BackupFailedCheck.SqliteIntegrityCheckFailed, "PRAGMA integrity_check failed against the artifact.");
        }

        return new BackupVerification(true, null, null);
    }

    private async Task<Workspace> ResolveWorkspaceAnchorAsync(WorkspaceId workspaceId, CancellationToken ct) =>
        await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct)
            ?? throw new BackupOperationException(
                BackupOperationException.WorkspaceAccessDenied, "The workspace could not be resolved for this operation.");

    private static void ValidateConfirmedSlug(string confirmedWorkspaceSlug, string actualSlug)
    {
        if (string.IsNullOrEmpty(confirmedWorkspaceSlug) ||
            confirmedWorkspaceSlug.Length > 100 ||
            !string.Equals(confirmedWorkspaceSlug, actualSlug, StringComparison.Ordinal))
        {
            throw new BackupOperationException(
                BackupOperationException.ValidationFailed, "The confirmed workspace slug does not match the target workspace.");
        }
    }

    /// <summary>Records <c>workspace.restore.failed</c> and returns the exception to throw for a
    /// precondition failure discovered after restore exclusivity was already acquired (so, unlike
    /// the anchor/slug gates, it is audited).</summary>
    private async Task<BackupOperationException> DenyRestoreAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, BackupOperationContext operation,
        string errorCode, string message, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEventRequest(
            workspaceId,
            operation.ActorId,
            operation.Channel,
            "workspace.restore.failed",
            "backup",
            artifact.BackupId.ToString(),
            operation.CorrelationId,
            $"failedCheck={errorCode}"), ct);

        return new BackupOperationException(errorCode, message);
    }

    /// <summary>Records <c>workspace.restore.failed</c> for one of the five documented artifact
    /// integrity causes (or, when <paramref name="errorCode"/> is supplied, the exclusive-access
    /// preflight failure) and returns the corresponding result/exception.</summary>
    private async Task<RestoreResult> RejectAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, BackupOperationContext operation,
        string? failedCheck, string detail, CancellationToken ct, string? errorCode = null)
    {
        await audit.RecordAsync(new AuditEventRequest(
            workspaceId,
            operation.ActorId,
            operation.Channel,
            "workspace.restore.failed",
            "backup",
            artifact.BackupId.ToString(),
            operation.CorrelationId,
            $"failedCheck={failedCheck ?? errorCode}"), ct);

        if (errorCode is not null)
        {
            throw new BackupOperationException(errorCode, detail);
        }

        return new RestoreResult(false, artifact.BackupId, DateTimeOffset.UtcNow, failedCheck, detail);
    }

    /// <summary>
    /// Runs after the swap: creates a fresh service scope and <see cref="IDbContextFactory{TContext}"/>
    /// context (the scoped <see cref="db"/> field must never be reused post-swap), migrates the
    /// restored database forward if it is older than this build, and writes the success audit event
    /// into the now-restored database.
    /// </summary>
    private async Task<(bool RollForwardApplied, DateTimeOffset RestoredAt)> MigrateAndAuditSuccessAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, InfraBackupManifestData manifestData,
        BackupOperationContext operation, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnvilboardDbContext>>();
        await using var freshDb = await factory.CreateDbContextAsync(ct);

        var pendingMigrations = await freshDb.Database.GetPendingMigrationsAsync(ct);
        var rollForwardApplied = pendingMigrations.Any();
        if (rollForwardApplied)
        {
            await freshDb.Database.MigrateAsync(ct);
        }

        var restoredAt = DateTimeOffset.UtcNow;
        await new AuditService(freshDb).RecordAsync(new AuditEventRequest(
            workspaceId,
            operation.ActorId,
            operation.Channel,
            "workspace.restore.completed",
            "backup",
            artifact.BackupId.ToString(),
            operation.CorrelationId,
            $"sourceCreatedAt={manifestData.CreatedAt:O};schemaVersion={manifestData.SchemaVersion};rollForwardApplied={rollForwardApplied}"), ct);

        return (rollForwardApplied, restoredAt);
    }

    /// <summary>
    /// Runs after a post-swap failure has already been rolled back to the safety copy: opens
    /// another fresh scope/context against the now-recovered live database and records
    /// <c>workspace.restore.failed</c> into it. Best-effort: a failure here is logged but never
    /// masks the original post-swap exception, since the caller already has a <see cref="RestoreResult"/>
    /// to return.
    /// </summary>
    private async Task RecordRestoreFailureInRecoveredDbAsync(
        WorkspaceId workspaceId, BackupArtifactRef artifact, BackupOperationContext operation, string detail, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnvilboardDbContext>>();
            await using var freshDb = await factory.CreateDbContextAsync(ct);

            await new AuditService(freshDb).RecordAsync(new AuditEventRequest(
                workspaceId,
                operation.ActorId,
                operation.Channel,
                "workspace.restore.failed",
                "backup",
                artifact.BackupId.ToString(),
                operation.CorrelationId,
                $"failedCheck={PostSwapFailure};detail={detail}"), ct);
        }
        catch (Exception auditEx) when (auditEx is not OperationCanceledException)
        {
            logger.LogError(
                auditEx, "Failed to record workspace.restore.failed after rollback for backup {BackupId}.", artifact.BackupId);
        }
    }

    private static BackupManifest ToApplicationManifest(InfraBackupManifestData data) => new(
        data.BackupId,
        data.RequestedForWorkspaceId,
        data.RequestedForWorkspaceSlug,
        data.Workspaces.Select(w => new BackupWorkspaceRef(w.Id, w.Slug)).ToList(),
        data.CreatedAt,
        data.ProductVersion,
        data.SchemaVersion,
        data.ChecksumSha256,
        data.SizeBytes);

    private string BuildSafetyCopyPath() =>
        $"{dbOptions.Value.DatabasePath}.pre-restore-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";

    private static string ResolveProductVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // Strip a "+{commitSha}" build-metadata suffix if present, keeping the semantic version.
            var plusIndex = informationalVersion.IndexOf('+');
            return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
