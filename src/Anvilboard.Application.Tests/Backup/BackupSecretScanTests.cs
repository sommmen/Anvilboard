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
/// AC-204 / NFR-SEC-001 (`docs/plans/backup-and-restore.md` §6, §7.5 invariant 6, §15): no
/// secret-shaped value may appear in a generated <c>backup-manifest.json</c> or in any
/// backup/restore <c>ResultSummary</c>. The corpus is produced by running the real stack against a
/// live database that has been deliberately seeded with secret-shaped values in exactly the places
/// a manifest or audit summary draws from — workspace name/slug and a long opaque token — rather
/// than by asserting on hand-written strings, so a future manifest or summary field that starts
/// echoing untrusted content fails this test instead of silently regressing the criterion.
/// </summary>
public sealed class BackupSecretScanTests : IAsyncLifetime
{
    /// <summary>64 hex chars: matches <c>SecretRedactor</c>'s opaque-value shape, and is also the
    /// exact shape of the manifest's legitimate SHA-256 checksum field. Named "token" rather than
    /// "secret" so the literal does not trip credential scanners; it is test data, not a credential.
    /// </summary>
    private const string OpaqueToken = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private const string WorkspaceSlug = "acme";

    private string _databasePath = null!;
    private string _connectionString = null!;
    private string _backupDirectory = null!;
    private AnvilboardDbOptions _dbOptions = null!;

    public Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-secretscan-{Guid.NewGuid():N}.db");
        _backupDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-secretscan-backups-{Guid.NewGuid():N}");
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
    public async Task GeneratedManifestAndAuditCorpus_ContainNoSecretShapedValues()
    {
        var workspaceId = await SeedWorkspaceWithSecretShapedContentAsync();
        var operation = new BackupOperationContext("agent:secret-scan", AuditChannel.Rest, $"corr-{OpaqueToken}");

        BackupManifest manifest;
        await using (var db = CreateContext())
        {
            manifest = await CreateService(db).CreateBackupAsync(workspaceId, operation);
        }

        // A failed restore (wrong-case confirmation is rejected before any artifact I/O) adds the
        // failure-path summary to the corpus alongside the success-path one.
        await using (var db = CreateContext())
        {
            await Assert.ThrowsAsync<BackupOperationException>(() => CreateService(db).RestoreAsync(
                workspaceId, new BackupArtifactRef(manifest.BackupId), WorkspaceSlug.ToUpperInvariant(), operation));
        }

        var manifestJson = await File.ReadAllTextAsync(ManifestPathFor(manifest.BackupId));

        // Negative control: prove the scan below can actually fail. Without this, a future change
        // that stops the manifest/summaries from carrying any content at all would leave the
        // assertions trivially satisfied and AC-204 unguarded.
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertNoSecretShapedValues(
            manifestJson.Replace("\"Slug\":", $"\"Token\":\"{OpaqueToken}\",\"Slug\":", StringComparison.Ordinal),
            manifest.ChecksumSha256,
            "negative-control"));

        AssertNoSecretShapedValues(manifestJson, manifest.ChecksumSha256, "backup-manifest.json");

        await using (var db = CreateContext())
        {
            var summaries = await db.AuditEvents
                .Where(e => e.Action.StartsWith("workspace.backup.") || e.Action.StartsWith("workspace.restore."))
                .Select(e => new { e.Action, e.ResultSummary })
                .ToListAsync();

            Assert.NotEmpty(summaries);
            foreach (var entry in summaries)
            {
                AssertNoSecretShapedValues(entry.ResultSummary ?? string.Empty, manifest.ChecksumSha256, entry.Action);
            }
        }
    }

    /// <summary>
    /// Asserts the text carries no secret-shaped value, in two independent ways: the seeded secret
    /// must be absent verbatim, and <see cref="SecretRedactor"/> must find nothing left to scrub.
    /// The manifest's own SHA-256 checksum is the one legitimately opaque 64-char value, so it is
    /// masked out before the redactor pass rather than being allowed to mask a real leak.
    /// </summary>
    private static void AssertNoSecretShapedValues(string text, string legitimateChecksum, string source)
    {
        Assert.True(
            !text.Contains(OpaqueToken, StringComparison.OrdinalIgnoreCase),
            $"{source} leaked the seeded secret verbatim: {text}");

        var withoutChecksum = text.Replace(legitimateChecksum, "<checksum>", StringComparison.OrdinalIgnoreCase);
        Assert.True(
            withoutChecksum == SecretRedactor.Scrub(withoutChecksum),
            $"{source} contains a secret-shaped value SecretRedactor would scrub: {withoutChecksum}");
    }

    private async Task<WorkspaceId> SeedWorkspaceWithSecretShapedContentAsync()
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
                Name = $"Acme apiKey={OpaqueToken}",
                Slug = WorkspaceSlug,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AuditEvents.Add(new AuditEvent
            {
                Id = AuditEventId.New(),
                WorkspaceId = workspaceId,
                ActorId = "user:seed",
                Channel = AuditChannel.Rest,
                Action = "workspace.integration.configured",
                TargetType = "integration",
                TargetId = Guid.NewGuid().ToString(),
                CorrelationId = "corr-seed",
                OccurredAt = DateTimeOffset.UtcNow,
                ResultSummary = $"token={OpaqueToken}",
            });
            await db.SaveChangesAsync();
        }

        return workspaceId;
    }

    private string ManifestPathFor(Guid backupId)
    {
        var store = new FileSystemBackupArchiveStore(
            Options.Create(_dbOptions), NullLogger<FileSystemBackupArchiveStore>.Instance);
        var location = store.FindBackup(backupId);
        Assert.NotNull(location);
        return location.ManifestPath;
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
