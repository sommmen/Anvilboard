using Anvilboard.Application.Auditing;
using Anvilboard.Application.Sync;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Tests.Sync;

/// <summary>
/// Options monitor that always answers with one fixed instance, for tests that care about the
/// configured values rather than about reload behaviour.
/// </summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Manually advanced clock, so staleness tests can cross a threshold without sleeping for it.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now += delta;
}

internal static class SyncTestDoubles
{
    /// <summary>
    /// Builds a health service over a real SQLite context and a real <see cref="AuditService"/>, so
    /// tests observe the audit rows actually written rather than a mock's expectations.
    /// </summary>
    public static IntegrationHealthService HealthService(
        AnvilboardDbContext db,
        TimeProvider? timeProvider = null,
        IngestionOptions? options = null) =>
        new(db,
            new StaticOptionsMonitor<IngestionOptions>(options ?? new IngestionOptions()),
            new AuditService(db),
            timeProvider ?? TimeProvider.System);
}
