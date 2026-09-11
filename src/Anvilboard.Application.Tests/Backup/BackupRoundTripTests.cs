using Anvilboard.Application.Auditing;
using Anvilboard.Application.Backup;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Tests.Backup;

/// <summary>
/// End-to-end backup -&gt; mutate -&gt; restore round trip against a real temporary SQLite file, using
/// the real Batch 1 <see cref="FileSystemBackupArchiveStore"/> and <see cref="SqliteBackupArchiver"/>
/// infrastructure (no fakes), so the checksum, integrity check, and atomic file swap are exercised
/// for real rather than simulated. <see cref="BackupServiceTests"/> covers orchestration-level
/// validation ordering, audit content, and failure mapping with fakes; this test is the one place
/// that proves the whole stack actually restores the live database's bytes.
/// </summary>
public sealed class BackupRoundTripTests : IAsyncLifetime
{
    private const string WorkspaceSlug = "acme";

    /// <summary>
    /// The migration immediately before this build's head, used to capture a backup at an older
    /// schema version. If a new migration is added, this stays valid as long as it still names a
    /// real, non-head migration; the test asserts a roll-forward actually happened, so a stale
    /// value that accidentally equals head would fail loudly rather than pass vacuously.
    /// </summary>
    private const string OlderMigration = "20260909100938_AddAuditEvents";

    private string _databasePath = null!;
    private string _connectionString = null!;
    private string _backupDirectory = null!;
    private AnvilboardDbOptions _dbOptions = null!;

    public Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-roundtrip-{Guid.NewGuid():N}.db");
        _backupDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-roundtrip-backups-{Guid.NewGuid():N}");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        _dbOptions = new AnvilboardDbOptions { DatabasePath = _databasePath, BackupDirectory = _backupDirectory };
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_databasePath);
        TryDelete($"{_databasePath}-wal");
        TryDelete($"{_databasePath}-shm");
        TryDelete($"{_databasePath}-journal");
        TryDeleteDirectory(_backupDirectory);

        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var safetyCopy in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_databasePath)}.pre-restore-*"))
            {
                TryDelete(safetyCopy);
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateBackupAsync_ThenRestoreAsync_RevertsTheLiveDatabaseToTheBackedUpState()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var workspaceId = WorkspaceId.New();
        await using (var db = CreateContext())
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Acme Corp Original",
                Slug = WorkspaceSlug,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Teams.Add(new Team
            {
                Id = TeamId.New(),
                WorkspaceId = workspaceId,
                Name = "Original Team",
                Key = "ORIG",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var operation = new BackupOperationContext("agent:roundtrip", AuditChannel.Rest, "corr-roundtrip");

        BackupManifest manifest;
        await using (var db = CreateContext())
        {
            manifest = await CreateService(db).CreateBackupAsync(workspaceId, operation);
        }

        Assert.NotEqual(Guid.Empty, manifest.BackupId);
        Assert.Equal(64, manifest.ChecksumSha256.Length);
        Assert.Single(manifest.Workspaces);

        // Mutate the live database after the backup without changing the *workspace set*: rename
        // the workspace and add a second team. A workspace-set change (e.g. a new workspace) would
        // instead have to fail the restore closed at the workspace-set-mismatch check.
        await using (var db = CreateContext())
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            workspace.Name = "Mutated After Backup";
            db.Teams.Add(new Team
            {
                Id = TeamId.New(),
                WorkspaceId = workspaceId,
                Name = "Post-Backup Team",
                Key = "POST",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            Assert.Equal(2, await db.Teams.CountAsync(t => t.WorkspaceId == workspaceId));
        }

        RestoreResult result;
        await using (var db = CreateContext())
        {
            result = await CreateService(db).RestoreAsync(
                workspaceId, new BackupArtifactRef(manifest.BackupId), WorkspaceSlug, operation);
        }

        Assert.True(result.Success);
        Assert.Null(result.FailedCheck);
        Assert.Equal(manifest.BackupId, result.BackupId);

        await using (var db = CreateContext())
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            Assert.Equal("Acme Corp Original", workspace.Name);

            var team = await db.Teams.SingleAsync(t => t.WorkspaceId == workspaceId);
            Assert.Equal("Original Team", team.Name);

            var auditEvent = await db.AuditEvents.AsNoTracking()
                .SingleAsync(e => e.Action == "workspace.restore.completed");
            Assert.Equal(manifest.BackupId.ToString(), auditEvent.TargetId);
            Assert.Equal("corr-roundtrip", auditEvent.CorrelationId);
        }
    }

    [Fact]
    public async Task RestoreAsync_WithATamperedArtifact_FailsClosedAndLeavesTheLiveDatabaseUntouched()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var workspaceId = WorkspaceId.New();
        await using (var db = CreateContext())
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Acme Corp",
                Slug = WorkspaceSlug,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var operation = new BackupOperationContext("agent:roundtrip", AuditChannel.Rest, "corr-tamper");
        BackupManifest manifest;
        await using (var db = CreateContext())
        {
            manifest = await CreateService(db).CreateBackupAsync(workspaceId, operation);
        }

        // Tamper with the on-disk snapshot after the fact so its bytes no longer match the
        // manifest's recorded checksum.
        var location = new FileSystemBackupArchiveStore(Options.Create(_dbOptions), NullLogger<FileSystemBackupArchiveStore>.Instance)
            .FindBackup(manifest.BackupId);
        Assert.NotNull(location);
        await File.AppendAllTextAsync(location!.DatabasePath, "tampered-bytes");

        RestoreResult result;
        await using (var db = CreateContext())
        {
            result = await CreateService(db).RestoreAsync(
                workspaceId, new BackupArtifactRef(manifest.BackupId), WorkspaceSlug, operation);
        }

        Assert.False(result.Success);
        Assert.Equal(BackupFailedCheck.ChecksumMismatch, result.FailedCheck);

        await using (var db = CreateContext())
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            Assert.Equal("Acme Corp", workspace.Name);
        }
    }

    /// <summary>
    /// An artifact captured at an <em>older</em> schema version must still restore, and must then be
    /// migrated forward to this build's schema. The backup is taken while the database sits at
    /// <see cref="OlderMigration"/> (one migration behind head), so the manifest records that older
    /// schema version; the restore therefore has to apply the remaining migration afterwards and
    /// report <c>rollForwardApplied=True</c>. Without this, only the already-current
    /// (<c>rollForwardApplied=False</c>) path is ever exercised.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_WithAnArtifactFromAnOlderSchemaVersion_RollsTheRestoredDatabaseForward()
    {
        // Migrate only as far as the second-to-last migration so the backup's recorded schema
        // version is genuinely older than this build's head.
        await using (var db = CreateContext())
        {
            await db.Database.GetService<IMigrator>().MigrateAsync(OlderMigration);
        }

        var workspaceId = WorkspaceId.New();
        await using (var db = CreateContext())
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Acme Corp Legacy",
                Slug = WorkspaceSlug,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var operation = new BackupOperationContext("agent:roundtrip", AuditChannel.Rest, "corr-rollforward");

        BackupManifest manifest;
        await using (var db = CreateContext())
        {
            manifest = await CreateService(db).CreateBackupAsync(workspaceId, operation);
        }

        Assert.Equal(OlderMigration, manifest.SchemaVersion);

        // Bring the live database up to head. The artifact is now one migration behind it.
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            workspace.Name = "Mutated At Head";
            await db.SaveChangesAsync();
        }

        RestoreResult result;
        await using (var db = CreateContext())
        {
            result = await CreateService(db).RestoreAsync(
                workspaceId, new BackupArtifactRef(manifest.BackupId), WorkspaceSlug, operation);
        }

        Assert.True(result.Success);
        Assert.Null(result.FailedCheck);

        await using (var verifyDb = CreateContext())
        {
            // The restored (older) content is present...
            var workspace = await verifyDb.Workspaces.SingleAsync(w => w.Id == workspaceId);
            Assert.Equal("Acme Corp Legacy", workspace.Name);

            // ...and the restored database was migrated forward to head, so nothing is pending.
            Assert.Empty(await verifyDb.Database.GetPendingMigrationsAsync());

            var auditEvent = await verifyDb.AuditEvents.AsNoTracking()
                .SingleAsync(e => e.Action == "workspace.restore.completed");
            Assert.Contains("rollForwardApplied=True", auditEvent.ResultSummary);
            Assert.Contains($"schemaVersion={OlderMigration}", auditEvent.ResultSummary);
        }
    }

    private AnvilboardDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(_connectionString).Options);

    private BackupService CreateService(AnvilboardDbContext db)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AnvilboardDbContext>(o => o.UseSqlite(_connectionString));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new BackupService(
            db,
            new FileSystemBackupArchiveStore(Options.Create(_dbOptions), NullLogger<FileSystemBackupArchiveStore>.Instance),
            new SqliteBackupArchiver(Options.Create(_dbOptions), NullLogger<SqliteBackupArchiver>.Instance),
            new RestoreCoordinator(),
            scopeFactory,
            Options.Create(_dbOptions),
            new AuditService(db),
            NullLogger<BackupService>.Instance);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Uniquely named temp directories are safe to leave for OS temp-file cleanup.
        }
    }
}
