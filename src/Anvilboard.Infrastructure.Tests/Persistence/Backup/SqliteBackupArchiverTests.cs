using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Tests.Persistence.Backup;

public sealed class SqliteBackupArchiverTests : IDisposable
{
    private readonly List<string> _pathsToClean = [];

    [Fact]
    public async Task CheckpointAsync_TruncatesTheWalFile()
    {
        var databasePath = CreateTrackedTempPath();

        // Keep this connection open across the whole test: SQLite folds/truncates the WAL file
        // when the *last* connection to a database closes, which would checkpoint away the
        // pending-writes state this test needs to observe before exercising the archiver's own
        // checkpoint. A still-open idle connection also matches the real deployment shape, where
        // the app's own connection(s) remain open while a backup runs.
        await using var keepAlive = await CreateWalDatabaseKeepOpenAsync(databasePath, rows: 300);
        var walPath = databasePath + "-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);

        var archiver = CreateArchiver(databasePath);
        await archiver.CheckpointAsync();

        var walSizeAfterCheckpoint = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        Assert.Equal(0, walSizeAfterCheckpoint);
    }

    [Fact]
    public async Task CheckpointAsync_IsANoOp_WhenTheDatabaseIsNotInWalMode()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 5);

        var archiver = CreateArchiver(databasePath);

        // Must not throw: PRAGMA wal_checkpoint is a documented no-op outside WAL mode.
        await archiver.CheckpointAsync();
        Assert.Equal(5, await CountRowsAsync(databasePath));
    }

    [Fact]
    public async Task SnapshotAsync_ProducesAnIndependentCopyWithTheSameData()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 5);
        var destinationPath = CreateTrackedTempPath();

        var archiver = CreateArchiver(databasePath);
        await archiver.SnapshotAsync(destinationPath);

        Assert.True(File.Exists(destinationPath));
        Assert.Equal(await CountRowsAsync(databasePath), await CountRowsAsync(destinationPath));

        // Independent: writing to the source after the snapshot must not change the snapshot.
        await InsertRowAsync(databasePath, "post-snapshot");
        Assert.NotEqual(await CountRowsAsync(databasePath), await CountRowsAsync(destinationPath));
    }

    [Fact]
    public async Task SnapshotAsync_IsConsistent_ForAWalModeDatabaseWithUncheckpointedWrites()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: true, rows: 7);
        var destinationPath = CreateTrackedTempPath();

        var archiver = CreateArchiver(databasePath);
        await archiver.SnapshotAsync(destinationPath);

        Assert.Equal(7, await CountRowsAsync(destinationPath));
    }

    [Fact]
    public async Task ComputeSha256Async_IsDeterministic_AndChangesWithContent()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 3);

        var archiver = CreateArchiver(databasePath);
        var firstHash = await archiver.ComputeSha256Async(databasePath);
        var secondHash = await archiver.ComputeSha256Async(databasePath);

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(64, firstHash.Length);
        Assert.Equal(firstHash, firstHash.ToLowerInvariant(), StringComparer.Ordinal);

        await InsertRowAsync(databasePath, "changes-the-file");
        var thirdHash = await archiver.ComputeSha256Async(databasePath);
        Assert.NotEqual(firstHash, thirdHash);
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ReturnsTrue_ForAHealthyDatabase()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 2);

        var archiver = CreateArchiver(databasePath);

        Assert.True(await archiver.VerifyIntegrityAsync(databasePath));
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ReturnsFalse_ForATruncatedFile()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 50);

        // Truncating a healthy multi-page file to a quarter of its size corrupts it in a way
        // `integrity_check`/open detects deterministically, independent of the exact page size the
        // running SQLite build uses (plan §7.4 "corrupted/truncated fixtures").
        var originalBytes = await File.ReadAllBytesAsync(databasePath);
        await File.WriteAllBytesAsync(databasePath, originalBytes[..(originalBytes.Length / 4)]);

        var archiver = CreateArchiver(databasePath);

        Assert.False(await archiver.VerifyIntegrityAsync(databasePath));
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ReturnsFalse_ForANonSqliteFile()
    {
        var databasePath = CreateTrackedTempPath();
        await File.WriteAllTextAsync(databasePath, "this is not a sqlite database at all");

        var archiver = CreateArchiver(databasePath);

        Assert.False(await archiver.VerifyIntegrityAsync(databasePath));
    }

    [Fact]
    public async Task TryAcquireExclusiveAccessAsync_ReturnsTrue_WhenNoOtherConnectionHoldsTheFile()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 1);

        var archiver = CreateArchiver(databasePath);

        Assert.True(await archiver.TryAcquireExclusiveAccessAsync());
    }

    [Fact]
    public async Task TryAcquireExclusiveAccessAsync_ReturnsFalse_WhenAnotherConnectionHoldsAnOpenWriteTransaction()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: false, rows: 1);

        await using var blockingConnection = new SqliteConnection(ConnectionString(databasePath));
        await blockingConnection.OpenAsync();
        await using (var pragma = blockingConnection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA locking_mode = EXCLUSIVE;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using (var begin = blockingConnection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; INSERT INTO Sample (Note) VALUES ('locked');";
            await begin.ExecuteNonQueryAsync();
        }

        var archiver = CreateArchiver(databasePath);

        Assert.False(await archiver.TryAcquireExclusiveAccessAsync());
    }

    [Fact]
    public async Task CreateSafetyCopyAsync_CopiesTheCurrentLiveContent()
    {
        var databasePath = CreateTrackedTempPath();
        await CreateDatabaseAsync(databasePath, walMode: true, rows: 10);
        var safetyCopyPath = CreateTrackedTempPath();

        var archiver = CreateArchiver(databasePath);
        await archiver.CreateSafetyCopyAsync(safetyCopyPath);

        Assert.True(File.Exists(safetyCopyPath));
        Assert.Equal(await CountRowsAsync(databasePath), await CountRowsAsync(safetyCopyPath));
    }

    [Fact]
    public async Task SwapInAsync_ReplacesLiveContent_LeavesTheStagedArtifactIntact_AndRemovesSidecars()
    {
        var databasePath = CreateTrackedTempPath();

        // Assert sidecar files exist while the connection is still open (see comment in
        // CheckpointAsync_TruncatesTheWalFile), then close it explicitly *before* swapping — a
        // real caller must ensure all of its own connections are closed/returned to the pool
        // before SwapInAsync's ClearAllPools()+File.Move can safely replace the live file.
        var keepAlive = await CreateWalDatabaseKeepOpenAsync(databasePath, rows: 3);
        Assert.True(File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"));
        await keepAlive.DisposeAsync();

        var stagedPath = CreateTrackedTempPath();
        await CreateDatabaseAsync(stagedPath, walMode: false, rows: 99);

        var archiver = CreateArchiver(databasePath);
        await archiver.SwapInAsync(stagedPath);

        Assert.Equal(99, await CountRowsAsync(databasePath));
        Assert.True(File.Exists(stagedPath));
        Assert.Equal(99, await CountRowsAsync(stagedPath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.False(File.Exists(databasePath + "-journal"));
    }

    private static SqliteBackupArchiver CreateArchiver(string liveDatabasePath) =>
        new(
            Options.Create(new AnvilboardDbOptions { DatabasePath = liveDatabasePath }),
            NullLogger<SqliteBackupArchiver>.Instance);

    private string CreateTrackedTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anvilboard-archiver-{Guid.NewGuid():N}.db");
        _pathsToClean.Add(path);
        return path;
    }

    private static async Task CreateDatabaseAsync(string databasePath, bool walMode, int rows)
    {
        await using var connection = await OpenWalAwareConnectionAsync(databasePath, walMode, rows);
    }

    /// <summary>
    /// Same fixture setup as <see cref="CreateDatabaseAsync"/>, but returns the still-open
    /// connection instead of disposing it, so callers can observe on-disk WAL/SHM state that
    /// would otherwise be folded away by SQLite's checkpoint-on-last-connection-close behavior.
    /// </summary>
    private static Task<SqliteConnection> CreateWalDatabaseKeepOpenAsync(string databasePath, int rows) =>
        OpenWalAwareConnectionAsync(databasePath, walMode: true, rows);

    private static async Task<SqliteConnection> OpenWalAwareConnectionAsync(string databasePath, bool walMode, int rows)
    {
        var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();

        if (walMode)
        {
            await using var walCommand = connection.CreateCommand();
            // Also disable SQLite's automatic checkpointing so the fixture's WAL file keeps
            // accumulating pending writes instead of being folded back on its own — tests need to
            // observe the *un*-checkpointed state before exercising the archiver's own checkpoint.
            walCommand.CommandText = "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0;";
            await walCommand.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Note TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < rows; i++)
        {
            await InsertRowCoreAsync(connection, $"row-{i}");
        }

        return connection;
    }

    private static async Task InsertRowAsync(string databasePath, string note)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();
        await InsertRowCoreAsync(connection, note);
    }

    private static async Task InsertRowCoreAsync(SqliteConnection connection, string note)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Sample (Note) VALUES ($note);";
        insert.Parameters.AddWithValue("$note", note);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath, readOnly: true));
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Sample;";
        return (long)(await count.ExecuteScalarAsync())!;
    }

    // Pooling keeps native sqlite3 handles (and any lock they hold) alive past Dispose, which
    // races with the archiver's own unpooled connections and with the raw FileStream reads used
    // for hashing/copying in these tests; disabling it makes fixture setup deterministic.
    private static string ConnectionString(string databasePath, bool readOnly = false) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _pathsToClean)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
            TryDelete(path + "-journal");
        }
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
}
