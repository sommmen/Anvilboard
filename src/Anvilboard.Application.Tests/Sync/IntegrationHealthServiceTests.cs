using Anvilboard.Application.Sync;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Sync;

/// <summary>
/// Covers <see cref="IntegrationHealthService"/> against the real SQLite schema
/// (<c>docs/plans/integration-sync-health.md</c> §8.2), including the audit policy that writes on
/// failure/recovery transitions only.
/// </summary>
public sealed class IntegrationHealthServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IngestionOptions Options = new()
    {
        PollInterval = TimeSpan.FromMinutes(1),
        StalenessThreshold = TimeSpan.FromMinutes(15),
    };

    [Fact]
    public async Task RecordAttempt_Success_MakesTheIntegrationFresh()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(fixture.Outcome(succeeded: true));

        var health = Assert.Single(await fixture.Service.GetHealthAsync(fixture.WorkspaceId));
        Assert.Equal(SyncCondition.Fresh, health.Condition);
        Assert.Equal(fixture.Clock.Now, health.LastSuccessAt);
        Assert.Equal(0, health.ConsecutiveFailureCount);
        Assert.Null(health.LastErrorCategory);
    }

    [Fact]
    public async Task RecordAttempt_ConsecutiveFailures_AccumulateTheCounter()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(
            fixture.Outcome(succeeded: false, category: SyncErrorCategory.Transport));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.RecordAttemptAsync(
            fixture.Outcome(succeeded: false, category: SyncErrorCategory.Transport));

        var health = Assert.Single(await fixture.Service.GetHealthAsync(fixture.WorkspaceId));
        Assert.Equal(SyncCondition.Failed, health.Condition);
        Assert.Equal(2, health.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task RecordAttempt_SuccessAfterFailure_ClearsTheFailureState()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(
            fixture.Outcome(succeeded: false, category: SyncErrorCategory.Auth));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.RecordAttemptAsync(fixture.Outcome(succeeded: true));

        var health = Assert.Single(await fixture.Service.GetHealthAsync(fixture.WorkspaceId));
        Assert.Equal(SyncCondition.Fresh, health.Condition);
        Assert.Equal(0, health.ConsecutiveFailureCount);
        Assert.Null(health.LastErrorCategory);
    }

    [Fact]
    public async Task GetHealth_SuccessOlderThanTheThreshold_BecomesStale()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(fixture.Outcome(succeeded: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(20));

        var health = Assert.Single(await fixture.Service.GetHealthAsync(fixture.WorkspaceId));
        Assert.Equal(SyncCondition.Stale, health.Condition);
    }

    [Fact]
    public async Task RecordAttempt_WritesOneAuditEventPerTransitionRatherThanPerAttempt()
    {
        // A provider polled every five minutes would write 288 identical rows a day if every failed
        // attempt were audited, burying the one event an operator actually needs: the transition.
        await using var fixture = await HealthFixture.CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            await fixture.Service.RecordAttemptAsync(
                fixture.Outcome(succeeded: false, category: SyncErrorCategory.Transport));
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await fixture.Service.RecordAttemptAsync(fixture.Outcome(succeeded: true));

        var actions = await fixture.Db.AuditEvents
            .AsNoTracking()
            .Where(e => e.Action.StartsWith("integration.sync."))
            .Select(e => e.Action)
            .ToListAsync();

        Assert.Equal(["integration.sync.failed", "integration.sync.recovered"], actions);
    }

    [Fact]
    public async Task RecordAttempt_AuditSummary_NeverCarriesTheProviderErrorMessage()
    {
        // A provider that echoes a credential back in an error string must not be able to write it
        // into the audit log through this path, so only the coarse category is recorded.
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(
            fixture.Outcome(succeeded: false, category: SyncErrorCategory.Auth));

        var summary = await fixture.Db.AuditEvents
            .AsNoTracking()
            .Where(e => e.Action == "integration.sync.failed")
            .Select(e => e.ResultSummary)
            .SingleAsync();

        Assert.Contains("Auth", summary);
        Assert.DoesNotContain("token", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordAttempt_ForADeletedIntegration_IsANoOpRatherThanAnError()
    {
        // The integration can be removed between an attempt starting and finishing. Writing health
        // for a row that no longer exists would violate the FK and resurrect deleted configuration.
        await using var fixture = await HealthFixture.CreateAsync();

        var outcome = new SyncAttemptOutcome(
            IntegrationId.New(),
            fixture.WorkspaceId,
            "github",
            fixture.Clock.Now,
            Succeeded: true);

        await fixture.Service.RecordAttemptAsync(outcome);

        Assert.Empty(await fixture.Db.IntegrationHealth.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RecordAttempt_SuccessCarryingAnErrorCategory_IsRejected()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RecordAttemptAsync(
            fixture.Outcome(succeeded: true) with { ErrorCategory = SyncErrorCategory.Auth }));
    }

    [Fact]
    public async Task ProvidersInCondition_ReturnsOnlyMatchingProviders()
    {
        await using var fixture = await HealthFixture.CreateAsync();

        await fixture.Service.RecordAttemptAsync(fixture.Outcome(succeeded: true));

        Assert.Equal(
            [IntegrationProvider.GitHub],
            await fixture.Service.ProvidersInConditionAsync(fixture.WorkspaceId, SyncCondition.Fresh));
        Assert.Empty(
            await fixture.Service.ProvidersInConditionAsync(fixture.WorkspaceId, SyncCondition.Failed));
    }

    private sealed class HealthFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private HealthFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            WorkspaceId workspaceId,
            IntegrationId integrationId,
            TestTimeProvider clock)
        {
            this.connection = connection;
            Db = db;
            WorkspaceId = workspaceId;
            IntegrationId = integrationId;
            Clock = clock;
            Service = SyncTestDoubles.HealthService(db, clock, Options);
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }
        public IntegrationId IntegrationId { get; }
        public TestTimeProvider Clock { get; }
        public IntegrationHealthService Service { get; }

        public SyncAttemptOutcome Outcome(bool succeeded, SyncErrorCategory? category = null) =>
            new(IntegrationId, WorkspaceId, "github", Clock.Now, succeeded, category);

        public static async Task<HealthFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AnvilboardDbContext(
                new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var integrationId = IntegrationId.New();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Workspace",
                Slug = "workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Integrations.Add(new Integration
            {
                Id = integrationId,
                WorkspaceId = workspaceId,
                Provider = IntegrationProvider.GitHub,
                SettingsJson = "{}",
                Status = IntegrationStatus.Enabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            return new HealthFixture(connection, db, workspaceId, integrationId, new TestTimeProvider(Start));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
