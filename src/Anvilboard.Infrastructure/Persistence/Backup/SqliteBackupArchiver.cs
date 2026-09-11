using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Persistence.Backup;

/// <summary>
/// Real SQLite implementation of <see cref="ISnapshotArchiver"/>: <c>PRAGMA wal_checkpoint</c>,
/// the SQLite Online Backup API, streamed SHA-256, <c>PRAGMA integrity_check</c>, an exclusive-lock
/// preflight, and pool-clearing atomic file swap (`docs/plans/backup-and-restore.md` §7.1, §8.3-§8.4).
/// </summary>
public sealed class SqliteBackupArchiver : ISnapshotArchiver
{
    // https://www.sqlite.org/rescode.html — SQLITE_BUSY and SQLITE_LOCKED primary result codes.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly IOptions<AnvilboardDbOptions> _dbOptions;
    private readonly ILogger<SqliteBackupArchiver> _logger;

    public SqliteBackupArchiver(IOptions<AnvilboardDbOptions> dbOptions, ILogger<SqliteBackupArchiver> logger)
    {
        _dbOptions = dbOptions;
        _logger = logger;
    }

    private string LiveDatabasePath => _dbOptions.Value.DatabasePath;

    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var connection = OpenConnection(LiveDatabasePath);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SnapshotAsync(string destinationDatabasePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetDirectoryName(destinationDatabasePath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await using var source = OpenConnection(LiveDatabasePath, readOnly: true);
        await source.OpenAsync(ct);

        await using var destination = OpenConnection(destinationDatabasePath);
        await destination.OpenAsync(ct);

        ct.ThrowIfCancellationRequested();

        // SqliteConnection.BackupDatabase is a synchronous native call with no cancellation
        // support mid-copy; it runs the whole online backup to completion once started.
        source.BackupDatabase(destination);
    }

    public async Task<string> ComputeSha256Async(string databasePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    public async Task<bool> VerifyIntegrityAsync(string databasePath, CancellationToken ct = default)
    {
        try
        {
            await using var connection = OpenConnection(databasePath, readOnly: true);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return false;
            }

            var firstResult = reader.GetString(0);
            return string.Equals(firstResult, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex)
        {
            // A file that is not a valid SQLite database, or is corrupted badly enough that SQLite
            // refuses to open or read it at all, is exactly the "not verified" outcome this method
            // exists to report — the caller maps it to `sqlite_integrity_check_failed`.
            _logger.LogInformation(ex, "Integrity check could not be completed for '{DatabasePath}'.", databasePath);
            return false;
        }
    }

    public async Task<bool> TryAcquireExclusiveAccessAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection(LiveDatabasePath);

        // Microsoft.Data.Sqlite treats a timeout of zero as "retry forever" rather than SQLite's
        // own "fail immediately" meaning for `PRAGMA busy_timeout = 0`, so a preflight check must
        // use a small *positive* timeout to fail fast: this bounds how long a restore/backup
        // attempt blocks when the live database is in fact busy, without being so tight that a
        // momentary lock (e.g. another connection mid-commit) reports a false negative.
        connection.DefaultTimeout = 1;

        try
        {
            await connection.OpenAsync(ct);

            // A no-op write transaction is the standard way to prove no other connection/process
            // currently holds a conflicting lock: BEGIN IMMEDIATE requires a RESERVED lock and
            // COMMIT requires escalating to EXCLUSIVE, so both readers and writers elsewhere cause
            // this to fail with SQLITE_BUSY/SQLITE_LOCKED instead of the pragma-only path used by
            // the app's own connections.
            await using var lockCommand = connection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE; COMMIT;";
            await lockCommand.ExecuteNonQueryAsync(ct);

            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            _logger.LogInformation(
                "Live database '{DatabasePath}' is busy or locked by another connection/process; " +
                "restore preflight failed closed.", LiveDatabasePath);
            return false;
        }
    }

    public async Task CreateSafetyCopyAsync(string safetyCopyPath, CancellationToken ct = default)
    {
        await CheckpointAsync(ct);
        ct.ThrowIfCancellationRequested();
        File.Copy(LiveDatabasePath, safetyCopyPath, overwrite: true);
    }

    public Task SwapInAsync(string stagedDatabasePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Pooled handles (e.g. from EF Core's connection pool) otherwise keep the live file locked
        // on Windows even after every caller has disposed its DbContext (plan §8.4).
        SqliteConnection.ClearAllPools();

        var liveDatabasePath = LiveDatabasePath;
        var stagingTempPath = $"{liveDatabasePath}.swap-{Guid.NewGuid():N}.tmp";

        // Stage the replacement next to the live file first so the final move is a same-volume,
        // near-instant rename rather than a copy performed while the live path is already gone.
        // The source (a historical backup artifact) is left untouched so it stays restorable again.
        File.Copy(stagedDatabasePath, stagingTempPath, overwrite: true);

        try
        {
            DeleteSidecarFiles(liveDatabasePath);
            File.Move(stagingTempPath, liveDatabasePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(stagingTempPath);
            throw;
        }

        return Task.CompletedTask;
    }

    private static SqliteConnection OpenConnection(string databasePath, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps the native sqlite3 handle (and any lock it holds) alive past Dispose,
            // which would defeat the exclusive-lock preflight and checkpoint/swap primitives below
            // (each of which relies on a lock being fully released as soon as its connection is
            // disposed). These are short-lived, low-frequency connections, so pooling has no
            // meaningful perf benefit here.
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }

    private static void DeleteSidecarFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            TryDeleteFile(databasePath + suffix);
        }
    }

    private static void TryDeleteFile(string path)
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
            // Best-effort cleanup; a lingering sidecar/temp file does not affect correctness of
            // the swap that already completed.
        }
    }
}
