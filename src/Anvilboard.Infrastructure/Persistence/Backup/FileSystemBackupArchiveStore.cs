using System.Text.Json;
using Anvilboard.Domain.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// First-release <see cref="IBackupArchiveStore"/>: one directory per backup on the local
/// filesystem, under <see cref="AnvilboardDbOptions.ResolveBackupDirectory"/>
/// (`docs/plans/backup-and-restore.md` §10.2-§10.3). Matches the plain-directory-per-backup
/// decision in plan §7.1: the snapshot stays directly openable as a SQLite file for verification,
/// without extracting an archive first.
/// </summary>
public sealed class FileSystemBackupArchiveStore : IBackupArchiveStore
{
    private const string DatabaseFileName = "anvilboard.db";
    private const string ManifestFileName = "backup-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();

    private readonly IOptions<AnvilboardDbOptions> _dbOptions;
    private readonly ILogger<FileSystemBackupArchiveStore> _logger;

    public FileSystemBackupArchiveStore(IOptions<AnvilboardDbOptions> dbOptions, ILogger<FileSystemBackupArchiveStore> logger)
    {
        _dbOptions = dbOptions;
        _logger = logger;
    }

    private string BackupDirectory => _dbOptions.Value.ResolveBackupDirectory();

    public Task EnsureWritableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var backupDirectory = BackupDirectory;
        try
        {
            Directory.CreateDirectory(backupDirectory);

            // Prove write access, not just that the directory exists: a read-only mount or ACL
            // denial must fail here, before any backup is attempted (plan §7.4).
            var probePath = Path.Combine(backupDirectory, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupStoreUnavailableException(
                $"Backup directory '{backupDirectory}' could not be created or is not writable.", ex);
        }

        return Task.CompletedTask;
    }

    public Task EnsureFreeSpaceAsync(long currentDatabaseSizeBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var backupDirectory = BackupDirectory;
        var requiredBytes = checked(currentDatabaseSizeBytes * 2);

        long availableBytes;
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var root = Path.GetPathRoot(Path.GetFullPath(backupDirectory));
            availableBytes = string.IsNullOrEmpty(root)
                ? throw new IOException($"Could not resolve a volume root for '{backupDirectory}'.")
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new BackupStoreUnavailableException(
                $"Could not determine free space for backup directory '{backupDirectory}'.", ex);
        }

        if (availableBytes < requiredBytes)
        {
            var shortfallBytes = requiredBytes - availableBytes;
            throw new BackupStoreUnavailableException(
                $"Backup directory '{backupDirectory}' has insufficient free space: needs " +
                $"{requiredBytes} bytes (2x the {currentDatabaseSizeBytes}-byte database) but only " +
                $"{availableBytes} bytes are available (shortfall {shortfallBytes} bytes).");
        }

        return Task.CompletedTask;
    }

    public BackupLocation CreateStagingLocation()
    {
        var backupId = Guid.NewGuid();
        var directoryName = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{backupId:N}";
        var directoryPath = Path.Combine(BackupDirectory, directoryName);
        Directory.CreateDirectory(directoryPath);

        return new BackupLocation(
            backupId,
            directoryPath,
            Path.Combine(directoryPath, DatabaseFileName),
            Path.Combine(directoryPath, ManifestFileName));
    }

    public async Task WriteManifestAsync(BackupLocation location, BackupManifestData manifest, CancellationToken ct = default)
    {
        await using var stream = File.Create(location.ManifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, ct);
    }

    public BackupLocation? FindBackup(Guid backupId)
    {
        var backupDirectory = BackupDirectory;
        if (!Directory.Exists(backupDirectory))
        {
            return null;
        }

        var suffix = backupId.ToString("N");
        var directoryPath = Directory.EnumerateDirectories(backupDirectory)
            .FirstOrDefault(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal));

        return directoryPath is null
            ? null
            : new BackupLocation(
                backupId,
                directoryPath,
                Path.Combine(directoryPath, DatabaseFileName),
                Path.Combine(directoryPath, ManifestFileName));
    }

    public async Task<BackupManifestData?> ReadManifestAsync(Guid backupId, CancellationToken ct = default)
    {
        var location = FindBackup(backupId);
        return location is null ? null : await TryReadManifestAsync(location, ct);
    }

    public async Task<IReadOnlyList<BackupManifestData>> ListBackupsAsync(CancellationToken ct = default)
    {
        var backupDirectory = BackupDirectory;
        if (!Directory.Exists(backupDirectory))
        {
            return [];
        }

        var manifests = new List<BackupManifestData>();
        foreach (var directoryPath in Directory.EnumerateDirectories(backupDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(directoryPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                // No manifest => not a complete backup (plan §7.4/§8.3); silently skipped.
                continue;
            }

            var location = new BackupLocation(
                Guid.Empty, directoryPath, Path.Combine(directoryPath, DatabaseFileName), manifestPath);
            var manifest = await TryReadManifestAsync(location, ct);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests.OrderByDescending(manifest => manifest.CreatedAt).ToList();
    }

    public bool TryDeleteIncompleteBackup(BackupLocation location)
    {
        try
        {
            if (Directory.Exists(location.DirectoryPath))
            {
                Directory.Delete(location.DirectoryPath, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Failed to delete incomplete backup directory '{DirectoryPath}'; leaving it for manual cleanup.",
                location.DirectoryPath);
            return false;
        }
    }

    private async Task<BackupManifestData?> TryReadManifestAsync(BackupLocation location, CancellationToken ct)
    {
        if (!File.Exists(location.ManifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(location.ManifestPath);
            return await JsonSerializer.DeserializeAsync<BackupManifestData>(stream, ManifestJsonOptions, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed backup manifest at '{ManifestPath}'.", location.ManifestPath);
            return null;
        }
    }

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new StronglyTypedIdJsonConverterFactory());
        return options;
    }
}
