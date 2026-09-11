using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Tests.Persistence.Backup;

public sealed class FileSystemBackupArchiveStoreTests : IDisposable
{
    private readonly string _backupDirectory =
        Path.Combine(Path.GetTempPath(), $"anvilboard-backup-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnsureWritableAsync_CreatesConfiguredDirectory()
    {
        var store = CreateStore();

        await store.EnsureWritableAsync();

        Assert.True(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task EnsureWritableAsync_ThrowsBackupStoreUnavailable_WhenPathIsBlockedByAFile()
    {
        // A plain file already sitting at the configured backup directory path can never become a
        // directory; this is a deterministic way to force the "unwritable" failure (plan §7.4).
        await File.WriteAllTextAsync(_backupDirectory, "not a directory");

        var store = CreateStore();

        await Assert.ThrowsAsync<BackupStoreUnavailableException>(() => store.EnsureWritableAsync());
    }

    [Fact]
    public async Task EnsureFreeSpaceAsync_ThrowsBackupStoreUnavailable_WhenRequiredExceedsAvailable()
    {
        var store = CreateStore();

        var exception = await Assert.ThrowsAsync<BackupStoreUnavailableException>(
            () => store.EnsureFreeSpaceAsync(long.MaxValue / 2));

        Assert.Contains("insufficient free space", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureFreeSpaceAsync_Succeeds_WhenRequiredIsSmall()
    {
        var store = CreateStore();

        await store.EnsureFreeSpaceAsync(1024);
    }

    [Fact]
    public void CreateStagingLocation_AllocatesUniqueDirectoryWithFixedFileNames()
    {
        var store = CreateStore();

        var location = store.CreateStagingLocation();

        Assert.True(Directory.Exists(location.DirectoryPath));
        Assert.Equal("anvilboard.db", Path.GetFileName(location.DatabasePath));
        Assert.Equal("backup-manifest.json", Path.GetFileName(location.ManifestPath));
        Assert.EndsWith(location.BackupId.ToString("N"), Path.GetFileName(location.DirectoryPath));
    }

    [Fact]
    public void CreateStagingLocation_AllocatesADistinctDirectoryEachCall()
    {
        var store = CreateStore();

        var first = store.CreateStagingLocation();
        var second = store.CreateStagingLocation();

        Assert.NotEqual(first.BackupId, second.BackupId);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
    }

    [Fact]
    public async Task WriteManifestAsync_ThenReadManifestAsync_RoundTripsExactly()
    {
        var store = CreateStore();
        var location = store.CreateStagingLocation();
        var manifest = CreateManifest(location.BackupId);

        await store.WriteManifestAsync(location, manifest);
        var read = await store.ReadManifestAsync(location.BackupId);

        Assert.NotNull(read);
        Assert.Equal(manifest.BackupId, read!.BackupId);
        Assert.Equal(manifest.RequestedForWorkspaceId, read.RequestedForWorkspaceId);
        Assert.Equal(manifest.RequestedForWorkspaceSlug, read.RequestedForWorkspaceSlug);
        Assert.Equal(manifest.CreatedAt, read.CreatedAt);
        Assert.Equal(manifest.ProductVersion, read.ProductVersion);
        Assert.Equal(manifest.SchemaVersion, read.SchemaVersion);
        Assert.Equal(manifest.ChecksumSha256, read.ChecksumSha256);
        Assert.Equal(manifest.SizeBytes, read.SizeBytes);
        Assert.Equal(manifest.Workspaces, read.Workspaces);
    }

    [Fact]
    public async Task ReadManifestAsync_ReturnsNull_ForUnknownBackupId()
    {
        var store = CreateStore();

        Assert.Null(await store.ReadManifestAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReadManifestAsync_ReturnsNull_ForAMalformedManifestFile()
    {
        var store = CreateStore();
        var location = store.CreateStagingLocation();
        await File.WriteAllTextAsync(location.ManifestPath, "{ not valid json");

        Assert.Null(await store.ReadManifestAsync(location.BackupId));
    }

    [Fact]
    public async Task ListBackupsAsync_ExcludesIncompleteDirectories_AndOrdersNewestFirst()
    {
        var store = CreateStore();

        var older = store.CreateStagingLocation();
        await store.WriteManifestAsync(older, CreateManifest(older.BackupId, DateTimeOffset.UtcNow.AddMinutes(-10)));

        var newer = store.CreateStagingLocation();
        await store.WriteManifestAsync(newer, CreateManifest(newer.BackupId, DateTimeOffset.UtcNow));

        // Incomplete: the staging directory exists (e.g. the process crashed mid-backup) but no
        // manifest was ever written, so it must never be surfaced as a usable backup (plan §8.3).
        var incomplete = store.CreateStagingLocation();

        var backups = await store.ListBackupsAsync();

        Assert.Equal(2, backups.Count);
        Assert.Equal(newer.BackupId, backups[0].BackupId);
        Assert.Equal(older.BackupId, backups[1].BackupId);
        Assert.DoesNotContain(backups, backup => backup.BackupId == incomplete.BackupId);
    }

    [Fact]
    public async Task ListBackupsAsync_ReturnsEmpty_WhenBackupDirectoryDoesNotExist()
    {
        var store = CreateStore();

        Assert.Empty(await store.ListBackupsAsync());
    }

    [Fact]
    public void FindBackup_ReturnsNull_WhenNoDirectoryExists()
    {
        var store = CreateStore();

        Assert.Null(store.FindBackup(Guid.NewGuid()));
    }

    [Fact]
    public void FindBackup_LocatesAnExistingStagingDirectoryById()
    {
        var store = CreateStore();
        var location = store.CreateStagingLocation();

        var found = store.FindBackup(location.BackupId);

        Assert.NotNull(found);
        Assert.Equal(location.DirectoryPath, found!.DirectoryPath);
        Assert.Equal(location.DatabasePath, found.DatabasePath);
        Assert.Equal(location.ManifestPath, found.ManifestPath);
    }

    [Fact]
    public void TryDeleteIncompleteBackup_RemovesTheStagingDirectory()
    {
        var store = CreateStore();
        var location = store.CreateStagingLocation();

        var deleted = store.TryDeleteIncompleteBackup(location);

        Assert.True(deleted);
        Assert.False(Directory.Exists(location.DirectoryPath));
    }

    [Fact]
    public void TryDeleteIncompleteBackup_ReturnsTrue_WhenDirectoryIsAlreadyGone()
    {
        var store = CreateStore();
        var location = store.CreateStagingLocation();
        Directory.Delete(location.DirectoryPath, recursive: true);

        Assert.True(store.TryDeleteIncompleteBackup(location));
    }

    private FileSystemBackupArchiveStore CreateStore() =>
        new(
            Options.Create(new AnvilboardDbOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), "unused-anvilboard.db"),
                BackupDirectory = _backupDirectory,
            }),
            NullLogger<FileSystemBackupArchiveStore>.Instance);

    private static BackupManifestData CreateManifest(Guid backupId, DateTimeOffset? createdAt = null)
    {
        var workspaceId = Anvilboard.Domain.WorkspaceId.New();
        return new BackupManifestData(
            backupId,
            workspaceId,
            "acme",
            [new BackupWorkspaceEntry(workspaceId, "acme")],
            createdAt ?? DateTimeOffset.UtcNow,
            "1.4.0",
            "20260909124004_AddPluginConfigAndState",
            "b1946ac92492d2347c6235b4d2611184",
            4_194_304);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_backupDirectory))
            {
                Directory.Delete(_backupDirectory, recursive: true);
            }
            else if (File.Exists(_backupDirectory))
            {
                File.Delete(_backupDirectory);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a uniquely named leftover temp directory is harmless.
        }
    }
}
