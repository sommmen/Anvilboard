namespace Anvilboard.Infrastructure.Persistence;

/// <summary>Bound from the "Database" configuration section.</summary>
public sealed class AnvilboardDbOptions
{
    /// <summary>
    /// Filesystem path to the SQLite database file. Defaults to a file next to the running
    /// executable so a self-contained publish + this one file is the entire deployable app.
    /// </summary>
    public string DatabasePath { get; set; } = "anvilboard.db";
}
