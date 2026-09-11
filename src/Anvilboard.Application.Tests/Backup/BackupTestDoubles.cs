using Anvilboard.Infrastructure.Persistence.Backup;

namespace Anvilboard.Application.Tests.Backup;

/// <summary>
/// Hand-rolled <see cref="IBackupArchiveStore"/> test double (this test project references no
/// mocking library). Every member delegates to an overridable <see cref="Func{T}"/> field
/// defaulting to a "healthy store" behavior, and every call is appended to <see cref="CallLog"/>
/// so a test can assert both the returned outcome and that no step ran before a fail-closed gate
/// that should have stopped it (e.g. no store I/O at all for an early workspace/slug denial).
/// </summary>
public sealed class FakeBackupArchiveStore : IBackupArchiveStore
{
    public List<string> CallLog { get; } = [];

    public Func<CancellationToken, Task> EnsureWritable { get; set; } = _ => Task.CompletedTask;
    public Func<long, CancellationToken, Task> EnsureFreeSpace { get; set; } = (_, _) => Task.CompletedTask;
    public Func<BackupLocation> OnCreateStagingLocation { get; set; } = () => CreateLocation(Guid.NewGuid());
    public Func<BackupLocation, BackupManifestData, CancellationToken, Task> OnWriteManifest { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<Guid, BackupLocation?> OnFindBackup { get; set; } = id => CreateLocation(id);
    public Func<Guid, CancellationToken, Task<BackupManifestData?>> OnReadManifest { get; set; } = (_, _) => Task.FromResult<BackupManifestData?>(null);
    public Func<CancellationToken, Task<IReadOnlyList<BackupManifestData>>> OnListBackups { get; set; } =
        _ => Task.FromResult<IReadOnlyList<BackupManifestData>>([]);
    public Func<BackupLocation, bool> OnTryDeleteIncompleteBackup { get; set; } = _ => true;

    public BackupManifestData? LastWrittenManifest { get; private set; }
    public BackupLocation? LastDeletedIncompleteBackup { get; private set; }

    public Task EnsureWritableAsync(CancellationToken ct = default)
    {
        CallLog.Add(nameof(EnsureWritableAsync));
        return EnsureWritable(ct);
    }

    public Task EnsureFreeSpaceAsync(long currentDatabaseSizeBytes, CancellationToken ct = default)
    {
        CallLog.Add(nameof(EnsureFreeSpaceAsync));
        return EnsureFreeSpace(currentDatabaseSizeBytes, ct);
    }

    public BackupLocation CreateStagingLocation()
    {
        CallLog.Add(nameof(CreateStagingLocation));
        return OnCreateStagingLocation();
    }

    public async Task WriteManifestAsync(BackupLocation location, BackupManifestData manifest, CancellationToken ct = default)
    {
        CallLog.Add(nameof(WriteManifestAsync));
        LastWrittenManifest = manifest;
        await OnWriteManifest(location, manifest, ct);
    }

    public BackupLocation? FindBackup(Guid backupId)
    {
        CallLog.Add(nameof(FindBackup));
        return OnFindBackup(backupId);
    }

    public Task<BackupManifestData?> ReadManifestAsync(Guid backupId, CancellationToken ct = default)
    {
        CallLog.Add(nameof(ReadManifestAsync));
        return OnReadManifest(backupId, ct);
    }

    public Task<IReadOnlyList<BackupManifestData>> ListBackupsAsync(CancellationToken ct = default)
    {
        CallLog.Add(nameof(ListBackupsAsync));
        return OnListBackups(ct);
    }

    public bool TryDeleteIncompleteBackup(BackupLocation location)
    {
        CallLog.Add(nameof(TryDeleteIncompleteBackup));
        LastDeletedIncompleteBackup = location;
        return OnTryDeleteIncompleteBackup(location);
    }

    /// <summary>Builds path-shaped (but never actually written) locations, since every archiver
    /// call is also faked in tests that use this store double.</summary>
    public static BackupLocation CreateLocation(Guid backupId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"anvilboard-fake-backup-{backupId:N}");
        return new BackupLocation(
            backupId, directory, Path.Combine(directory, "anvilboard.db"), Path.Combine(directory, "backup-manifest.json"));
    }
}

/// <summary>
/// Hand-rolled <see cref="ISnapshotArchiver"/> test double, mirroring <see cref="FakeBackupArchiveStore"/>:
/// every member is overridable and every call is logged, so tests can inject a failure at exactly
/// one step of the fail-closed restore sequence while every earlier step behaves normally.
/// </summary>
public sealed class FakeSnapshotArchiver : ISnapshotArchiver, IDisposable
{
    public List<string> CallLog { get; } = [];
    public List<string> SwapInPaths { get; } = [];

    /// <summary>
    /// Directories the default <see cref="OnSnapshot"/> actually created on disk, so
    /// <see cref="Dispose"/> can remove them. Without this the staging directories accumulate
    /// under the system temp root, one per test run, forever.
    /// </summary>
    private readonly List<string> _createdDirectories = [];

    public FakeSnapshotArchiver()
    {
        // Writes a small real file by default so that BackupService's post-snapshot `new
        // FileInfo(location.DatabasePath).Length` call (used to record the manifest's size) does
        // not throw FileNotFoundException, mirroring the real SqliteBackupArchiver always
        // producing a file. The directory it creates is tracked so Dispose can remove it.
        OnSnapshot = (destinationPath, _) =>
        {
            var directory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(directory);
            _createdDirectories.Add(directory);
            File.WriteAllBytes(destinationPath, [1, 2, 3, 4]);
            return Task.CompletedTask;
        };
    }

    /// <summary>Removes the staging directories the default snapshot behavior created.</summary>
    public void Dispose()
    {
        foreach (var directory in _createdDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Uniquely named temp directories are safe to leave for OS temp-file cleanup.
            }
        }

        _createdDirectories.Clear();
    }

    public Func<CancellationToken, Task> OnCheckpoint { get; set; } = _ => Task.CompletedTask;
    public Func<string, CancellationToken, Task> OnSnapshot { get; set; }
    public Func<string, CancellationToken, Task<string>> OnComputeSha256 { get; set; } =
        (_, _) => Task.FromResult(new string('a', 64));
    public Func<string, CancellationToken, Task<bool>> OnVerifyIntegrity { get; set; } = (_, _) => Task.FromResult(true);
    public Func<CancellationToken, Task<bool>> OnTryAcquireExclusiveAccess { get; set; } = _ => Task.FromResult(true);
    public Func<string, CancellationToken, Task> OnCreateSafetyCopy { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task> OnSwapIn { get; set; } = (_, _) => Task.CompletedTask;

    public Task CheckpointAsync(CancellationToken ct = default)
    {
        CallLog.Add(nameof(CheckpointAsync));
        return OnCheckpoint(ct);
    }

    public Task SnapshotAsync(string destinationDatabasePath, CancellationToken ct = default)
    {
        CallLog.Add(nameof(SnapshotAsync));
        return OnSnapshot(destinationDatabasePath, ct);
    }

    public Task<string> ComputeSha256Async(string databasePath, CancellationToken ct = default)
    {
        CallLog.Add(nameof(ComputeSha256Async));
        return OnComputeSha256(databasePath, ct);
    }

    public Task<bool> VerifyIntegrityAsync(string databasePath, CancellationToken ct = default)
    {
        CallLog.Add(nameof(VerifyIntegrityAsync));
        return OnVerifyIntegrity(databasePath, ct);
    }

    public Task<bool> TryAcquireExclusiveAccessAsync(CancellationToken ct = default)
    {
        CallLog.Add(nameof(TryAcquireExclusiveAccessAsync));
        return OnTryAcquireExclusiveAccess(ct);
    }

    public Task CreateSafetyCopyAsync(string safetyCopyPath, CancellationToken ct = default)
    {
        CallLog.Add(nameof(CreateSafetyCopyAsync));
        return OnCreateSafetyCopy(safetyCopyPath, ct);
    }

    public Task SwapInAsync(string stagedDatabasePath, CancellationToken ct = default)
    {
        CallLog.Add(nameof(SwapInAsync));
        SwapInPaths.Add(stagedDatabasePath);
        return OnSwapIn(stagedDatabasePath, ct);
    }
}
