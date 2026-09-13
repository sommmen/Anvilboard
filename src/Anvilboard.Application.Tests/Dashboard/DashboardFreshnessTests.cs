using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Sync;
using Anvilboard.Application.Tests.Sync;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Dashboard;

/// <summary>
/// Covers the freshness caveat the dashboard carries alongside its counts
/// (<c>docs/plans/integration-sync-health.md</c> §8.7): a quiet board and a broken board must not
/// look alike.
/// </summary>
public sealed class DashboardFreshnessTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IngestionOptions Options = new()
    {
        PollInterval = TimeSpan.FromMinutes(1),
        StalenessThreshold = TimeSpan.FromMinutes(15),
    };

    [Fact]
    public async Task Summary_WithNoIntegrations_ReportsAllZeroes()
    {
        await using var fixture = await DashboardFixture.CreateAsync();

        var summary = await fixture.Service.GetSummaryAsync(fixture.WorkspaceId);

        var freshness = summary.IntegrationFreshness;
        Assert.Equal(0, freshness.Fresh);
        Assert.Equal(0, freshness.Stale);
        Assert.Equal(0, freshness.Paused);
        Assert.Equal(0, freshness.Failed);
        Assert.Null(freshness.OldestSuccessfulSyncAt);
    }

    [Fact]
    public async Task Summary_CountsEachIntegrationUnderItsOwnCondition()
    {
        await using var fixture = await DashboardFixture.CreateAsync();
        var github = await fixture.AddIntegrationAsync(IntegrationProvider.GitHub, IntegrationStatus.Enabled);
        var linear = await fixture.AddIntegrationAsync(IntegrationProvider.Linear, IntegrationStatus.Paused);

        await fixture.Health.RecordAttemptAsync(
            new SyncAttemptOutcome(github, fixture.WorkspaceId, "github", fixture.Clock.Now, Succeeded: true));
        await fixture.Health.RecordAttemptAsync(
            new SyncAttemptOutcome(linear, fixture.WorkspaceId, "linear", fixture.Clock.Now, Succeeded: true));

        var freshness = (await fixture.Service.GetSummaryAsync(fixture.WorkspaceId)).IntegrationFreshness;

        Assert.Equal(1, freshness.Fresh);
        Assert.Equal(1, freshness.Paused);
        Assert.Equal(0, freshness.Stale);
        Assert.Equal(0, freshness.Failed);
    }

    [Fact]
    public async Task Summary_OldestSuccessfulSync_IsTheWeakestLinkNotTheMostRecentOne()
    {
        // Reporting the newest success would let one healthy integration mask several that stopped
        // days ago, which is precisely the illusion this field exists to prevent.
        await using var fixture = await DashboardFixture.CreateAsync();
        var github = await fixture.AddIntegrationAsync(IntegrationProvider.GitHub, IntegrationStatus.Enabled);
        var linear = await fixture.AddIntegrationAsync(IntegrationProvider.Linear, IntegrationStatus.Enabled);

        var older = fixture.Clock.Now;
        await fixture.Health.RecordAttemptAsync(
            new SyncAttemptOutcome(github, fixture.WorkspaceId, "github", older, Succeeded: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Health.RecordAttemptAsync(
            new SyncAttemptOutcome(linear, fixture.WorkspaceId, "linear", fixture.Clock.Now, Succeeded: true));

        var freshness = (await fixture.Service.GetSummaryAsync(fixture.WorkspaceId)).IntegrationFreshness;

        Assert.Equal(older, freshness.OldestSuccessfulSyncAt);
    }

    [Fact]
    public async Task Summary_AFailingIntegration_IsCountedAsFailedNotStale()
    {
        await using var fixture = await DashboardFixture.CreateAsync();
        var github = await fixture.AddIntegrationAsync(IntegrationProvider.GitHub, IntegrationStatus.Enabled);

        await fixture.Health.RecordAttemptAsync(new SyncAttemptOutcome(
            github,
            fixture.WorkspaceId,
            "github",
            fixture.Clock.Now,
            Succeeded: false,
            SyncErrorCategory.Auth));

        var freshness = (await fixture.Service.GetSummaryAsync(fixture.WorkspaceId)).IntegrationFreshness;

        Assert.Equal(1, freshness.Failed);
        Assert.Equal(0, freshness.Stale);
    }

    private sealed class DashboardFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly AnvilboardDbContext db;

        private DashboardFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            WorkspaceId workspaceId,
            TestTimeProvider clock)
        {
            this.connection = connection;
            this.db = db;
            WorkspaceId = workspaceId;
            Clock = clock;
            Health = SyncTestDoubles.HealthService(db, clock, Options);
            Service = new DashboardService(db, Health);
        }

        public WorkspaceId WorkspaceId { get; }
        public TestTimeProvider Clock { get; }
        public IntegrationHealthService Health { get; }
        public DashboardService Service { get; }

        public async Task<IntegrationId> AddIntegrationAsync(
            IntegrationProvider provider,
            IntegrationStatus status)
        {
            var id = IntegrationId.New();
            db.Integrations.Add(new Integration
            {
                Id = id,
                WorkspaceId = WorkspaceId,
                Provider = provider,
                SettingsJson = "{}",
                Status = status,
                CreatedAt = Clock.Now,
                UpdatedAt = Clock.Now,
            });
            await db.SaveChangesAsync();
            return id;
        }

        public static async Task<DashboardFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AnvilboardDbContext(
                new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Workspace",
                Slug = "workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            return new DashboardFixture(connection, db, workspaceId, new TestTimeProvider(Start));
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
