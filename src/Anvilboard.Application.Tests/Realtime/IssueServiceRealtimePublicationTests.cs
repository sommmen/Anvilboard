using Anvilboard.Application.Automation;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Realtime;
using Anvilboard.Application.Workflows;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anvilboard.Application.Tests.Realtime;

/// <summary>
/// Covers TC-RT-001: a committed issue mutation publishes a workspace-scoped, versioned change, a
/// denied mutation publishes nothing, and a failing publisher cannot fail the mutation.
/// </summary>
public sealed class IssueServiceRealtimePublicationTests
{
    [Fact]
    public async Task CreateAsync_PublishesCreatedIssueChangeScopedToTheOwningWorkspace()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);

        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Publish me");

        var change = Assert.Single(publisher.Changes.OfType<RealtimeIssueChange>());
        Assert.Equal(fixture.WorkspaceId, change.WorkspaceId);
        Assert.Equal(issue.Id, change.IssueId);
        Assert.Equal(RealtimeIssueChangeKind.Created, change.ChangeKind);
        Assert.Equal(issue.Version, change.Version);
        Assert.Equal("issue.changed", change.EventType);
    }

    [Fact]
    public async Task CreateAsync_PublishesAMatchingActivityChangeForTheSameIssue()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);

        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Publish me");

        var activity = Assert.Single(publisher.Changes.OfType<RealtimeActivityChange>());
        Assert.Equal(issue.Id, activity.IssueId);
        Assert.Equal(fixture.WorkspaceId, activity.WorkspaceId);
        Assert.NotEqual(default, activity.ActivityEventId);
    }

    [Fact]
    public async Task ChangeStatusAsync_PublishesTheIncrementedVersionAsAnUpdate()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        fixture.AllowTransitionToDone();
        await fixture.Db.SaveChangesAsync();

        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Move me");
        publisher.Changes.Clear();

        var updated = await service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, IssueStatus.Done);

        var change = Assert.Single(publisher.Changes.OfType<RealtimeIssueChange>());
        Assert.Equal(RealtimeIssueChangeKind.Updated, change.ChangeKind);
        Assert.Equal(updated.Version, change.Version);
        Assert.Equal(1, change.Version);
    }

    [Fact]
    public async Task AssignAsync_PublishesTheIncrementedVersionAsAnUpdate()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Assign me");
        publisher.Changes.Clear();

        var updated = await service.AssignAsync(fixture.WorkspaceId, issue.Id, MemberId.New());

        var change = Assert.Single(publisher.Changes.OfType<RealtimeIssueChange>());
        Assert.Equal(1, updated.Version);
        Assert.Equal(updated.Version, change.Version);
    }

    [Fact]
    public async Task UpsertFromExternalAsync_ChangedIssue_PublishesTheIncrementedVersionAsAnUpdate()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var original = new NormalizedIssue(IntegrationProvider.GitHub, "repo#1", "RT", "Original", null, IssueStatus.Backlog, IssuePriority.None, null, null, [], "one", DateTimeOffset.UtcNow);
        var created = await service.UpsertFromExternalUnscopedAsync(original);
        publisher.Changes.Clear();

        var updated = await service.UpsertFromExternalUnscopedAsync(original with { Title = "Changed", SyncFingerprint = "two" });

        var change = Assert.Single(publisher.Changes.OfType<RealtimeIssueChange>());
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(1, updated.Version);
        Assert.Equal(updated.Version, change.Version);
    }

    [Fact]
    public async Task UpsertFromExternalAsync_ExistingLinkOwnedByDifferentWorkspace_ThrowsInsteadOfCrossTenantMutation()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);

        // File the issue via the untargeted overload first, so the external link ends up owned by
        // the fixture's own workspace/team.
        var original = new NormalizedIssue(IntegrationProvider.GitHub, "cross-tenant#1", "RT", "Original", null, IssueStatus.Backlog, IssuePriority.None, null, null, [], "one", DateTimeOffset.UtcNow);
        await service.UpsertFromExternalUnscopedAsync(original);

        // A second workspace that happens to also configure a team keyed "RT" — team keys are only
        // unique within a workspace, so this is a legal, if coincidental, configuration.
        var otherWorkspaceId = WorkspaceId.New();
        fixture.Db.Workspaces.Add(new Workspace
        {
            Id = otherWorkspaceId,
            Name = "Other workspace",
            Slug = "other-workspace",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        fixture.Db.Teams.Add(new Team
        {
            Id = TeamId.New(),
            WorkspaceId = otherWorkspaceId,
            Name = "Other team",
            Key = "RT",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        // Same (Provider, SourceKey) dedupe key as the existing link, but now resolved against the
        // *other* workspace — as would happen if a trusted webhook's team-key routing pointed at a
        // different tenant. This must be refused rather than silently mutating the first
        // workspace's issue (the cross-tenant `ExternalLink` mutation this regression test guards).
        var sameLinkDifferentTenant = original with { Title = "Hijacked", SyncFingerprint = "two" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpsertFromExternalAsync(otherWorkspaceId, sameLinkDifferentTenant));
    }

    [Fact]
    public async Task UpsertFromExternalAsync_UnscopedTeamKeyMatchesMultipleWorkspaces_ThrowsAmbiguousKeyException()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);

        // A second workspace that also configures a team keyed "RT" — legal, since team keys are
        // only unique within a workspace. `SyncCoordinator`'s ingestion-polling path uses the
        // unscoped overload (no workspace to disambiguate with), so a key shared across two
        // workspaces is genuinely ambiguous and must fail closed with a clear message rather than
        // an unhandled framework `InvalidOperationException` from `SingleOrDefaultAsync` or a
        // silent pick of whichever team happens to sort first.
        var otherWorkspaceId = WorkspaceId.New();
        fixture.Db.Workspaces.Add(new Workspace
        {
            Id = otherWorkspaceId,
            Name = "Other workspace",
            Slug = "other-workspace-ambiguous",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        fixture.Db.Teams.Add(new Team
        {
            Id = TeamId.New(),
            WorkspaceId = otherWorkspaceId,
            Name = "Other team",
            Key = "RT",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var normalized = new NormalizedIssue(IntegrationProvider.GitHub, "ambiguous#1", "RT", "Ambiguous", null, IssueStatus.Backlog, IssuePriority.None, null, null, [], "one", DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpsertFromExternalUnscopedAsync(normalized));

        Assert.Contains("more than one workspace", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(publisher.Changes);
    }

    [Fact]
    public async Task ChangeStatusAsync_DeniedTransition_PublishesNothing()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Do not move me");
        publisher.Changes.Clear();

        await Assert.ThrowsAsync<WorkflowTransitionDeniedException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, IssueStatus.Done));

        Assert.Empty(publisher.Changes);
    }

    [Fact]
    public async Task AddCommentAsync_PublishesActivityForAnIssueWhoseTeamWasNotPreloaded()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Comment on me");
        publisher.Changes.Clear();

        await service.AddCommentAsync(fixture.WorkspaceId, issue.Id, "A comment");

        var activity = Assert.Single(publisher.Changes.OfType<RealtimeActivityChange>());
        Assert.Equal(fixture.WorkspaceId, activity.WorkspaceId);
        Assert.Equal(issue.Id, activity.IssueId);
    }

    [Fact]
    public async Task CreateAsync_WhenPublisherThrows_StillReturnsTheCommittedIssue()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var service = fixture.CreateService(new ThrowingRealtimeUpdatePublisher());

        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Survive publication failure");

        Assert.Equal(issue.Id, (await fixture.Db.Issues.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task AddCommentAsync_WhenTheTeamDisappears_RejectsTheCommentInsteadOfWritingIt()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Comment on me");

        // Workspace membership is established by the issue's team. Once the team is gone the issue
        // is no longer provably inside the caller's workspace, so the scoped lookup must fail
        // closed rather than append a comment that nobody can attribute to a tenant. (Before
        // MAJ-022 this path resolved the workspace lazily at publication time, so the comment was
        // written first and only the realtime notification failed.)
        await fixture.RemoveTeamAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddCommentAsync(fixture.WorkspaceId, issue.Id, "A comment"));

        Assert.Empty(await fixture.Db.Comments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_PropagatesTheAmbientCorrelationId()
    {
        await using var fixture = await RealtimeFixture.CreateAsync();
        var publisher = new RecordingRealtimeUpdatePublisher();
        var service = fixture.CreateService(publisher, CorrelationContext.FromHeaderOrNew("corr-123"));

        await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Trace me");

        Assert.All(publisher.Changes, change => Assert.Equal("corr-123", change.CorrelationId));
    }

    private sealed class RecordingRealtimeUpdatePublisher : IRealtimeUpdatePublisher
    {
        public List<RealtimeChange> Changes { get; } = [];

        public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default)
        {
            Changes.Add(change);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingRealtimeUpdatePublisher : IRealtimeUpdatePublisher
    {
        public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default) =>
            throw new InvalidOperationException("Transport unavailable.");
    }

    private sealed class FakePluginRegistry : IPluginRegistry
    {
        public IReadOnlyList<IAnvilboardPlugin> All { get; } = [];
        public IReadOnlyList<IIngestionSource> IngestionSources { get; } = [];
        public IReadOnlyList<IWebhookReceiver> WebhookReceivers { get; } = [];
        public IReadOnlyList<IIssueHook> IssueHooks { get; } = [];
    }

    private sealed class RealtimeFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly WorkflowState backlog;
        private readonly WorkflowState done;

        private RealtimeFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            WorkspaceId workspaceId,
            TeamId teamId,
            WorkflowState backlog,
            WorkflowState done)
        {
            this.connection = connection;
            this.backlog = backlog;
            this.done = done;
            Db = db;
            WorkspaceId = workspaceId;
            TeamId = teamId;
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }
        public TeamId TeamId { get; }

        public IssueService CreateService(
            IRealtimeUpdatePublisher publisher,
            CorrelationContext? correlationContext = null) => new(
            Db,
            new FakePluginRegistry(),
            new WorkflowEngine(Db),
            publisher,
            correlationContext ?? CorrelationContext.FromHeaderOrNew(null),
            NullLogger<IssueService>.Instance);

        /// <summary>
        /// Deletes the team so workspace resolution from a team id fails, without disturbing issues
        /// that already reference it.
        /// </summary>
        public async Task RemoveTeamAsync()
        {
            await Db.Teams.Where(team => team.Id == TeamId).ExecuteDeleteAsync();
            Db.ChangeTracker.Clear();
        }

        public void AllowTransitionToDone() => Db.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = WorkspaceId,
            FromStateId = backlog.Id,
            ToStateId = done.Id,
        });

        public static async Task<RealtimeFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Realtime workspace",
                Slug = "realtime-workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var teamId = TeamId.New();
            db.Teams.Add(new Team
            {
                Id = teamId,
                WorkspaceId = workspaceId,
                Name = "Realtime team",
                Key = "RT",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var backlog = new WorkflowState
            {
                Id = WorkflowStateId.New(),
                WorkspaceId = workspaceId,
                Key = "backlog",
                DisplayName = "Backlog",
                Order = 0,
            };
            var done = new WorkflowState
            {
                Id = WorkflowStateId.New(),
                WorkspaceId = workspaceId,
                Key = "done",
                DisplayName = "Done",
                Order = 1,
                IsTerminal = true,
            };
            db.WorkflowStates.AddRange(backlog, done);
            await db.SaveChangesAsync();

            return new RealtimeFixture(connection, db, workspaceId, teamId, backlog, done);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
