using Anvilboard.Application.Auditing;
using Anvilboard.Application.Backup;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Tests.Backup;

/// <summary>
/// Covers <see cref="BackupService"/>'s fail-closed validation ordering, audit emission/content,
/// failure mapping, and post-swap rollback, using the hand-rolled <see cref="FakeBackupArchiveStore"/>/
/// <see cref="FakeSnapshotArchiver"/> doubles so each test can fail exactly one step while every
/// earlier step behaves normally. A real temporary-file SQLite database (migrated for real) backs
/// every test, because the restore schema-version check depends on a real
/// <c>__EFMigrationsHistory</c> table that <c>EnsureCreatedAsync</c> never populates. The real
/// backup-store/archiver infrastructure and a genuine file swap are instead covered end-to-end by
/// <see cref="BackupRoundTripTests"/>.
/// </summary>
public sealed class BackupServiceTests : IAsyncLifetime
{
    private const string WorkspaceSlug = "acme-corp";

    private string _databasePath = null!;
    private string _connectionString = null!;
    private AnvilboardDbContext _db = null!;
    private WorkspaceId _workspaceId;

    public FakeBackupArchiveStore Store { get; } = new();
    public FakeSnapshotArchiver Archiver { get; } = new();
    public RestoreCoordinator Coordinator { get; } = new();

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-backupsvc-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        _db = new AnvilboardDbContext(new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(_connectionString).Options);
        await _db.Database.MigrateAsync();

        _workspaceId = WorkspaceId.New();
        _db.Workspaces.Add(new Workspace
        {
            Id = _workspaceId,
            Name = "Acme Corp",
            Slug = WorkspaceSlug,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Archiver.Dispose();
        TryDelete(_databasePath);
        TryDelete($"{_databasePath}-wal");
        TryDelete($"{_databasePath}-shm");
        TryDelete($"{_databasePath}-journal");
    }

    // ---- CreateBackupAsync ----------------------------------------------------------------

    [Fact]
    public async Task CreateBackupAsync_UnknownWorkspace_ThrowsWorkspaceAccessDenied_WithNoStoreOrArchiverIO()
    {
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.CreateBackupAsync(WorkspaceId.New(), NewOperation()));

        Assert.Equal(BackupOperationException.WorkspaceAccessDenied, ex.ErrorCode);
        Assert.Empty(Store.CallLog);
        Assert.Empty(Archiver.CallLog);
        Assert.Equal(0, await _db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task CreateBackupAsync_StoreUnwritable_WrapsAsArtifactStoreUnavailable_BeforeAnyBackupIdExists()
    {
        Store.EnsureWritable = _ => throw new BackupStoreUnavailableException("disk full");
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.CreateBackupAsync(_workspaceId, NewOperation()));

        Assert.Equal(BackupOperationException.ArtifactStoreUnavailable, ex.ErrorCode);
        Assert.Equal([nameof(FakeBackupArchiveStore.EnsureWritableAsync)], Store.CallLog);
        Assert.Empty(Archiver.CallLog);

        // No BackupId exists yet at this point, so nothing can be audited against (plan §11
        // assumes a backupId target; this batch's documented interpretation is "no audit before
        // a BackupId exists").
        Assert.Equal(0, await _db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task CreateBackupAsync_InsufficientFreeSpace_WrapsAsArtifactStoreUnavailable_BeforeStagingLocationIsCreated()
    {
        Store.EnsureFreeSpace = (_, _) => throw new BackupStoreUnavailableException("not enough free space");
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.CreateBackupAsync(_workspaceId, NewOperation()));

        Assert.Equal(BackupOperationException.ArtifactStoreUnavailable, ex.ErrorCode);
        Assert.DoesNotContain(nameof(FakeBackupArchiveStore.CreateStagingLocation), Store.CallLog);
        Assert.Equal(0, await _db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task CreateBackupAsync_SnapshotFails_DeletesIncompleteBackup_RecordsFailureAudit_AndRethrowsUnwrapped()
    {
        var thrown = new IOException("disk error mid snapshot");
        Archiver.OnSnapshot = (_, _) => throw thrown;
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<IOException>(() => service.CreateBackupAsync(_workspaceId, NewOperation()));
        Assert.Same(thrown, ex);

        Assert.Contains(nameof(FakeBackupArchiveStore.TryDeleteIncompleteBackup), Store.CallLog);
        Assert.NotNull(Store.LastDeletedIncompleteBackup);
        Assert.DoesNotContain(nameof(FakeBackupArchiveStore.WriteManifestAsync), Store.CallLog);

        var auditEvent = await _db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("workspace.backup.failed", auditEvent.Action);
        Assert.Equal("backup", auditEvent.TargetType);
        Assert.Equal(Store.LastDeletedIncompleteBackup!.BackupId.ToString(), auditEvent.TargetId);
        Assert.Contains(nameof(IOException), auditEvent.ResultSummary);
    }

    [Fact]
    public async Task CreateBackupAsync_Success_WritesManifestAndRecordsCreatedAuditWithExpectedFields()
    {
        var service = BuildService();
        var operation = NewOperation(actorId: "agent:alice", correlationId: "corr-99");

        var manifest = await service.CreateBackupAsync(_workspaceId, operation);

        Assert.NotEqual(Guid.Empty, manifest.BackupId);
        Assert.Equal(_workspaceId, manifest.RequestedForWorkspaceId);
        Assert.Equal(WorkspaceSlug, manifest.RequestedForWorkspaceSlug);
        var workspaceRef = Assert.Single(manifest.Workspaces);
        Assert.Equal(_workspaceId, workspaceRef.Id);
        Assert.NotEqual("none", manifest.SchemaVersion);
        Assert.NotEmpty(manifest.ChecksumSha256);
        Assert.NotNull(Store.LastWrittenManifest);
        Assert.Equal(manifest.BackupId, Store.LastWrittenManifest!.BackupId);

        var auditEvent = await _db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("workspace.backup.created", auditEvent.Action);
        Assert.Equal("backup", auditEvent.TargetType);
        Assert.Equal(manifest.BackupId.ToString(), auditEvent.TargetId);
        Assert.Equal("agent:alice", auditEvent.ActorId);
        Assert.Equal(AuditChannel.Rest, auditEvent.Channel);
        Assert.Equal("corr-99", auditEvent.CorrelationId);
        Assert.Contains("schemaVersion=", auditEvent.ResultSummary);
        Assert.Contains("sizeBytes=", auditEvent.ResultSummary);
        Assert.Contains("checksumPrefix=", auditEvent.ResultSummary);
    }

    // ---- RestoreAsync: early gates (no I/O, no audit) --------------------------------------

    [Fact]
    public async Task RestoreAsync_UnknownWorkspace_ThrowsWorkspaceAccessDenied_WithNoIOOrAudit()
    {
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreAsync(
            WorkspaceId.New(), new BackupArtifactRef(Guid.NewGuid()), WorkspaceSlug, NewOperation()));

        Assert.Equal(BackupOperationException.WorkspaceAccessDenied, ex.ErrorCode);
        Assert.Empty(Store.CallLog);
        Assert.Empty(Archiver.CallLog);
        Assert.Equal(0, await _db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task RestoreAsync_ConfirmedSlugMismatch_ThrowsValidationFailed_WithNoIOOrAudit()
    {
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreAsync(
            _workspaceId, new BackupArtifactRef(Guid.NewGuid()), "not-the-right-slug", NewOperation()));

        Assert.Equal(BackupOperationException.ValidationFailed, ex.ErrorCode);
        Assert.Empty(Store.CallLog);
        Assert.Equal(0, await _db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task RestoreAsync_EmptyConfirmedSlug_ThrowsValidationFailed()
    {
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreAsync(
            _workspaceId, new BackupArtifactRef(Guid.NewGuid()), string.Empty, NewOperation()));

        Assert.Equal(BackupOperationException.ValidationFailed, ex.ErrorCode);
    }

    // ---- RestoreAsync: exclusivity / existence gates (audited) -----------------------------

    [Fact]
    public async Task RestoreAsync_AnotherRestoreAlreadyInProgress_ThrowsRateLimited_AndRecordsAudit()
    {
        using var existingRestore = Coordinator.TryBeginRestore();
        Assert.NotNull(existingRestore);

        var service = BuildService();
        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreAsync(
            _workspaceId, new BackupArtifactRef(Guid.NewGuid()), WorkspaceSlug, NewOperation()));

        Assert.Equal(BackupOperationException.RateLimited, ex.ErrorCode);
        Assert.Empty(Store.CallLog);

        var auditEvent = await _db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("workspace.restore.failed", auditEvent.Action);
        Assert.Contains("RATE_LIMITED", auditEvent.ResultSummary);
    }

    [Fact]
    public async Task RestoreAsync_UnknownBackupId_ThrowsReferencedEntityNotFound_AndRecordsAudit()
    {
        Store.OnFindBackup = _ => null;
        var service = BuildService();
        var artifact = new BackupArtifactRef(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.RestoreAsync(_workspaceId, artifact, WorkspaceSlug, NewOperation()));

        Assert.Equal(BackupOperationException.ReferencedEntityNotFound, ex.ErrorCode);

        var auditEvent = await _db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("workspace.restore.failed", auditEvent.Action);
        Assert.Equal(artifact.BackupId.ToString(), auditEvent.TargetId);
        Assert.Contains("REFERENCED_ENTITY_NOT_FOUND", auditEvent.ResultSummary);

        // Coordinator's restore-exclusivity lease must be released even on this early-thrown path.
        using var nextRestore = Coordinator.TryBeginRestore();
        Assert.NotNull(nextRestore);
    }

    // ---- RestoreAsync: the five fail-closed integrity checks (return RestoreResult) --------

    [Fact]
    public async Task RestoreAsync_ManifestMissing_ReturnsManifestMissingOrMalformed_WithoutTouchingLiveDatabase()
    {
        var backupId = Guid.NewGuid();
        Store.OnFindBackup = id => id == backupId ? FakeBackupArchiveStore.CreateLocation(id) : null;
        Store.OnReadManifest = (_, _) => Task.FromResult<BackupManifestData?>(null);
        var service = BuildService();

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.ManifestMissingOrMalformed, result.FailedCheck);
        Assert.Empty(Archiver.SwapInPaths);

        var auditEvent = await _db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Contains(BackupFailedCheck.ManifestMissingOrMalformed, auditEvent.ResultSummary);
    }

    [Fact]
    public async Task RestoreAsync_ChecksumMismatch_ReturnsChecksumMismatch_WithoutTouchingLiveDatabase()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        Archiver.OnComputeSha256 = (_, _) => Task.FromResult(new string('b', 64));
        var service = BuildService();

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.ChecksumMismatch, result.FailedCheck);
        Assert.Empty(Archiver.SwapInPaths);
    }

    [Fact]
    public async Task RestoreAsync_IntegrityCheckFails_ReturnsSqliteIntegrityCheckFailed_WithoutTouchingLiveDatabase()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        Archiver.OnVerifyIntegrity = (_, _) => Task.FromResult(false);
        var service = BuildService();

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.SqliteIntegrityCheckFailed, result.FailedCheck);
        Assert.Empty(Archiver.SwapInPaths);
    }

    [Fact]
    public async Task RestoreAsync_SchemaVersionUnknownToThisBuild_ReturnsSchemaVersionIncompatible_WithoutTouchingLiveDatabase()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId) with { SchemaVersion = "20990101000000_FromTheFuture" };
        ConfigureHappyPathRestore(backupId, manifest);
        var service = BuildService();

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.SchemaVersionIncompatible, result.FailedCheck);
        Assert.Empty(Archiver.SwapInPaths);
    }

    [Fact]
    public async Task RestoreAsync_WorkspaceSetMismatch_ReturnsWorkspaceSetMismatch_WithNoOverride()
    {
        var backupId = Guid.NewGuid();
        // The manifest's workspace set does not include this host's live workspace, simulating a
        // backup taken before the live workspace existed (or on a different host).
        var manifest = await BuildValidManifestDataAsync(backupId) with
        {
            Workspaces = [new BackupWorkspaceEntry(WorkspaceId.New(), "some-other-workspace")],
        };
        ConfigureHappyPathRestore(backupId, manifest);
        var service = BuildService();

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.WorkspaceSetMismatch, result.FailedCheck);
        Assert.Empty(Archiver.SwapInPaths);
    }

    // ---- RestoreAsync: post-validation preflight and outcomes ------------------------------

    [Fact]
    public async Task RestoreAsync_LiveDatabaseBusy_ThrowsArtifactStoreUnavailable_AndReopensAdmission()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        Archiver.OnTryAcquireExclusiveAccess = _ => Task.FromResult(false);
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreAsync(
            _workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation()));

        Assert.Equal(BackupOperationException.ArtifactStoreUnavailable, ex.ErrorCode);
        Assert.Empty(Archiver.SwapInPaths);

        using var databaseOperation = Coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(databaseOperation);
        using var nextRestore = Coordinator.TryBeginRestore();
        Assert.NotNull(nextRestore);
    }

    [Fact]
    public async Task RestoreAsync_Success_SwapsInTheArtifactAndRecordsCompletedAuditIntoTheFreshContext()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        var service = BuildService(CreateWorkingScopeFactory());
        var operation = NewOperation(correlationId: "corr-success");
        var before = DateTimeOffset.UtcNow;

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, operation);

        Assert.True(result.Success);
        Assert.Equal(backupId, result.BackupId);
        Assert.Null(result.FailedCheck);
        Assert.InRange(result.RestoredAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Single(Archiver.SwapInPaths);
        Assert.Contains(nameof(FakeSnapshotArchiver.CreateSafetyCopyAsync), Archiver.CallLog);

        // Admission is reopened and the restore lease released once the restore completes.
        using var databaseOperation = Coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(databaseOperation);
        using var nextRestore = Coordinator.TryBeginRestore();
        Assert.NotNull(nextRestore);

        // The success audit event is written into a *fresh* post-swap context, not the original
        // scoped `_db`, but both point at the same underlying file.
        await using var verifyDb = new AnvilboardDbContext(
            new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(_connectionString).Options);
        var auditEvent = await verifyDb.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("workspace.restore.completed", auditEvent.Action);
        Assert.Equal(backupId.ToString(), auditEvent.TargetId);
        Assert.Equal("corr-success", auditEvent.CorrelationId);
        Assert.Contains("rollForwardApplied=False", auditEvent.ResultSummary);
    }

    [Fact]
    public async Task RestoreAsync_PostSwapFailure_RollsBackToTheSafetyCopy_AndReturnsAFailedResult()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);

        var throwingScopeFactory = new SingleServiceScopeFactory(new SingleServiceProvider(new ThrowingDbContextFactory()));
        var service = BuildService(throwingScopeFactory);

        var result = await service.RestoreAsync(_workspaceId, new BackupArtifactRef(backupId), WorkspaceSlug, NewOperation());

        Assert.False(result.Success);
        Assert.Equal(backupId, result.BackupId);
        // Deliberately not one of the five BackupFailedCheck values (see BackupService's
        // PostSwapFailure constant): a post-swap migration/audit failure is not one of the plan's
        // documented pre-swap integrity causes.
        Assert.Equal("post_swap_failure", result.FailedCheck);
        Assert.Contains("boom-post-swap", result.Detail);

        // Forward swap, then a rollback swap back to the safety copy: two distinct paths.
        Assert.Equal(2, Archiver.SwapInPaths.Count);
        Assert.NotEqual(Archiver.SwapInPaths[0], Archiver.SwapInPaths[1]);

        // Admission must still be reopened even though the post-swap step failed.
        using var databaseOperation = Coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(databaseOperation);
        using var nextRestore = Coordinator.TryBeginRestore();
        Assert.NotNull(nextRestore);
    }

    // ---- ListBackupsAsync / VerifyBackupAsync ----------------------------------------------

    [Fact]
    public async Task ListBackupsAsync_UnknownWorkspace_ThrowsWorkspaceAccessDenied()
    {
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.ListBackupsAsync(WorkspaceId.New()));
        Assert.Equal(BackupOperationException.WorkspaceAccessDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task ListBackupsAsync_MapsEveryManifestFromTheStore()
    {
        var backupId = Guid.NewGuid();
        var manifestData = await BuildValidManifestDataAsync(backupId);
        Store.OnListBackups = _ => Task.FromResult<IReadOnlyList<BackupManifestData>>([manifestData]);
        var service = BuildService();

        var manifests = await service.ListBackupsAsync(_workspaceId);

        var manifest = Assert.Single(manifests);
        Assert.Equal(backupId, manifest.BackupId);
        Assert.Equal(manifestData.SchemaVersion, manifest.SchemaVersion);
    }

    [Fact]
    public async Task VerifyBackupAsync_UnknownBackupId_ThrowsReferencedEntityNotFound()
    {
        Store.OnFindBackup = _ => null;
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<BackupOperationException>(
            () => service.VerifyBackupAsync(_workspaceId, new BackupArtifactRef(Guid.NewGuid())));
        Assert.Equal(BackupOperationException.ReferencedEntityNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyBackupAsync_HealthyArtifact_ReturnsVerifiedTrue()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        var service = BuildService();

        var verification = await service.VerifyBackupAsync(_workspaceId, new BackupArtifactRef(backupId));

        Assert.True(verification.Verified);
        Assert.Null(verification.FailedCheck);
    }

    [Fact]
    public async Task VerifyBackupAsync_ChecksumMismatch_ReturnsChecksumMismatch()
    {
        var backupId = Guid.NewGuid();
        var manifest = await BuildValidManifestDataAsync(backupId);
        ConfigureHappyPathRestore(backupId, manifest);
        Archiver.OnComputeSha256 = (_, _) => Task.FromResult(new string('c', 64));
        var service = BuildService();

        var verification = await service.VerifyBackupAsync(_workspaceId, new BackupArtifactRef(backupId));

        Assert.False(verification.Verified);
        Assert.Equal(BackupFailedCheck.ChecksumMismatch, verification.FailedCheck);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static BackupOperationContext NewOperation(string actorId = "agent:test", string correlationId = "corr-1") =>
        new(actorId, AuditChannel.Rest, correlationId);

    private async Task<BackupManifestData> BuildValidManifestDataAsync(Guid backupId)
    {
        var appliedMigrations = await _db.Database.GetAppliedMigrationsAsync();
        var schemaVersion = appliedMigrations.First();

        return new BackupManifestData(
            backupId,
            _workspaceId,
            WorkspaceSlug,
            [new BackupWorkspaceEntry(_workspaceId, WorkspaceSlug)],
            DateTimeOffset.UtcNow,
            "1.0.0",
            schemaVersion,
            new string('a', 64),
            1_234);
    }

    /// <summary>Wires the fakes so every one of restore's fail-closed checks passes; individual
    /// tests then override exactly one member to force exactly one check to fail.</summary>
    private void ConfigureHappyPathRestore(Guid backupId, BackupManifestData manifest)
    {
        var location = FakeBackupArchiveStore.CreateLocation(backupId);
        Store.OnFindBackup = id => id == backupId ? location : null;
        Store.OnReadManifest = (id, _) => Task.FromResult(id == backupId ? manifest : null);
        Archiver.OnComputeSha256 = (_, _) => Task.FromResult(manifest.ChecksumSha256);
        Archiver.OnVerifyIntegrity = (_, _) => Task.FromResult(true);
    }

    private BackupService BuildService(IServiceScopeFactory? scopeFactory = null) => new(
        _db,
        Store,
        Archiver,
        Coordinator,
        scopeFactory ?? CreateWorkingScopeFactory(),
        Options.Create(new AnvilboardDbOptions { DatabasePath = _databasePath }),
        new AuditService(_db),
        NullLogger<BackupService>.Instance);

    private IServiceScopeFactory CreateWorkingScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AnvilboardDbContext>(o => o.UseSqlite(_connectionString));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Uniquely named temp files are safe to leave for OS temp-file cleanup.
        }
    }

    /// <summary>Forces the post-swap step to fail deterministically without any real database or
    /// file-system corruption: resolving <see cref="IDbContextFactory{TContext}"/> from this scope
    /// always throws.</summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<AnvilboardDbContext>
    {
        public AnvilboardDbContext CreateDbContext() => throw new InvalidOperationException("boom-post-swap");

        public Task<AnvilboardDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom-post-swap");
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class SingleServiceScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;

        public void Dispose()
        {
        }
    }

    private sealed class SingleServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SingleServiceScope(provider);
    }
}
