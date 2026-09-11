using Anvilboard.Infrastructure.Persistence;

namespace Anvilboard.Infrastructure.Tests.Persistence.Backup;

public sealed class AnvilboardDbOptionsBackupTests
{
    [Fact]
    public void ResolveBackupDirectory_DefaultsToBackupsFolderNextToDatabasePath()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-opts-{Guid.NewGuid():N}");
        var options = new AnvilboardDbOptions { DatabasePath = Path.Combine(databaseDirectory, "anvilboard.db") };

        var resolved = options.ResolveBackupDirectory();

        Assert.Equal(Path.Combine(databaseDirectory, "backups"), resolved);
    }

    [Fact]
    public void ResolveBackupDirectory_PrefersExplicitlyConfiguredDirectory()
    {
        var explicitDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-explicit-{Guid.NewGuid():N}");
        var options = new AnvilboardDbOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-{Guid.NewGuid():N}.db"),
            BackupDirectory = explicitDirectory,
        };

        Assert.Equal(explicitDirectory, options.ResolveBackupDirectory());
    }

    [Fact]
    public void ResolveBackupDirectory_ResolvesRelativeDatabasePathAgainstItsFullDirectory()
    {
        var options = new AnvilboardDbOptions { DatabasePath = "anvilboard.db" };

        var resolved = options.ResolveBackupDirectory();

        var expectedDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath("anvilboard.db"))!, "backups");
        Assert.Equal(expectedDirectory, resolved);
    }
}
