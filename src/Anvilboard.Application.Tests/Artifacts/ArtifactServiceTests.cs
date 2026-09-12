using System.Text;
using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Artifacts;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Artifacts;

public sealed class ArtifactServiceTests
{
    [Fact]
    public async Task AttachArtifactAsync_PersistsArtifactAndActivityEvent()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var actorId = MemberId.New();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.Deployment,
            "Staging deploy",
            "https://deploy.example/123",
            actorId: actorId);

        Assert.Equal("deployment", artifact.Kind);
        Assert.Equal("Staging deploy", artifact.Title);
        Assert.Equal("https://deploy.example/123", artifact.ContentReference);
        Assert.Equal("local", artifact.Source);
        Assert.Equal(actorId.Value, artifact.AddedById);

        var stored = await fixture.Db.Artifacts.SingleAsync();
        Assert.Equal(fixture.Issue.Id, stored.IssueId);
        Assert.Equal(ArtifactKind.Deployment, stored.Kind);

        var activity = await fixture.Db.ActivityEvents.SingleAsync();
        Assert.Equal(ActivityEventType.ArtifactAttached, activity.Type);
        Assert.Equal(fixture.Issue.Id, activity.IssueId);
        Assert.Equal(actorId, activity.ActorId);
    }

    [Fact]
    public async Task AttachArtifactAsync_AutomationProvenanceIsDistinguishable()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.Link,
            "Slack thread",
            "https://slack.example/thread",
            source: "slack-thread-expansion");

        Assert.Equal("slack-thread-expansion", artifact.Source);
        Assert.Null(artifact.AddedById);

        // The hook path emits the same audited event a human attach does — provenance is the only
        // difference between them.
        var activity = await fixture.Db.ActivityEvents.SingleAsync();
        Assert.Equal(ActivityEventType.ArtifactAttached, activity.Type);
        Assert.Null(activity.ActorId);
    }

    [Fact]
    public async Task AttachArtifactAsync_StoresInlineContentThroughStore()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var bytes = Encoding.UTF8.GetBytes("crash log contents");

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.File,
            "crash.log",
            inlineContent: new ArtifactInlineContent(bytes, "text/plain"));

        Assert.Equal(fixture.Store.LastReference, artifact.ContentReference);
        Assert.Equal(bytes, fixture.Store.Stored[artifact.ContentReference].Bytes);

        // The raw content must never leak into the row or the activity payload.
        var activity = await fixture.Db.ActivityEvents.SingleAsync();
        Assert.DoesNotContain("crash log contents", activity.DataJson);
    }

    [Fact]
    public async Task AttachArtifactAsync_StoreFailurePersistsNothing()
    {
        await using var fixture = await ArtifactFixture.CreateAsync(new ThrowingArtifactStore());
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.File,
            "crash.log",
            inlineContent: new ArtifactInlineContent([1, 2, 3], "application/octet-stream")));

        Assert.Equal("ARTIFACT_STORE_UNAVAILABLE", ex.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.ToListAsync());
        Assert.Empty(await fixture.Db.ActivityEvents.ToListAsync());
    }

    [Theory]
    [InlineData("", "https://example.test")]
    [InlineData("   ", "https://example.test")]
    [InlineData("Title", "")]
    [InlineData("Title", "   ")]
    public async Task AttachArtifactAsync_RejectsEmptyRequiredFields(string title, string contentReference)
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, ArtifactKind.Link, title, contentReference));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.ToListAsync());
    }

    [Fact]
    public async Task AttachArtifactAsync_RejectsUndefinedKind()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, (ArtifactKind)42, "Title", "https://example.test"));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifactAsync_RejectsOversizedMetadata()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.PullRequest,
            "PR",
            "https://example.test",
            metadata: new string('x', (8 * 1024) + 1)));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifactAsync_RejectsBothContentReferenceAndInlineContent()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.File,
            "crash.log",
            "https://example.test",
            new ArtifactInlineContent([1, 2, 3])));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifactAsync_RejectsUnknownIssue()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            IssueId.New(), ArtifactKind.Link, "Title", "https://example.test"));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifactAsync_RejectsIssueNotReachableThroughATeam()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.OrphanedIssue.Id, ArtifactKind.Link, "Title", "https://example.test"));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.ToListAsync());
    }

    [Fact]
    public async Task ListArtifactsAsync_RejectsIssueNotReachableThroughATeam()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(
            () => service.ListArtifactsAsync(fixture.OrphanedIssue.Id));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task ListArtifactsAsync_ReturnsOnlyIssueArtifactsOldestFirst()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AttachArtifactAsync(fixture.Issue.Id, ArtifactKind.Link, "First", "https://a.test");
        await service.AttachArtifactAsync(fixture.Issue.Id, ArtifactKind.Link, "Second", "https://b.test");
        await service.AttachArtifactAsync(fixture.OtherIssue.Id, ArtifactKind.Link, "Elsewhere", "https://c.test");

        var artifacts = await service.ListArtifactsAsync(fixture.Issue.Id);

        Assert.Equal(["First", "Second"], artifacts.Select(artifact => artifact.Title));
    }

    [Fact]
    public async Task ListArtifactsAsync_RejectsUnknownIssue()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.ListArtifactsAsync(IssueId.New()));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task RefreshArtifactAsync_FirstEventAttaches()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.RefreshArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.PullRequest,
            "github:acme/app#7",
            "Fix the thing",
            "https://github.test/acme/app/pull/7",
            """{"state":"open"}""");

        Assert.Equal("github:acme/app#7", artifact.DedupKey);
        Assert.Equal("github", artifact.Source);
        Assert.Single(await fixture.Db.Artifacts.ToListAsync());

        var activity = await fixture.Db.ActivityEvents.SingleAsync();
        Assert.Equal(ActivityEventType.ArtifactAttached, activity.Type);
    }

    [Fact]
    public async Task RefreshArtifactAsync_SubsequentEventUpdatesInPlace()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        const string DedupKey = "github:acme/app#7";

        var first = await service.RefreshArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, DedupKey, "Fix the thing",
            "https://github.test/acme/app/pull/7", """{"state":"open"}""");

        var second = await service.RefreshArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, DedupKey, "Fix the thing (merged)",
            "https://github.test/acme/app/pull/7", """{"state":"merged"}""");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("""{"state":"merged"}""", second.Metadata);
        Assert.Equal("Fix the thing (merged)", second.Title);

        // The dedup key upserts rather than accumulating a row per provider event.
        Assert.Single(await fixture.Db.Artifacts.ToListAsync());

        var types = await fixture.Db.ActivityEvents.Select(activity => activity.Type).ToListAsync();
        Assert.Equal([ActivityEventType.ArtifactAttached, ActivityEventType.ArtifactRefreshed], types);
    }

    [Fact]
    public async Task RefreshArtifactAsync_PreservesOriginalActor()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var actorId = MemberId.New();
        const string DedupKey = "github:acme/app#9";

        await service.AttachArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, "Manual PR link",
            "https://github.test/acme/app/pull/9", actorId: actorId, dedupKey: DedupKey);

        var refreshed = await service.RefreshArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, DedupKey, "Manual PR link",
            "https://github.test/acme/app/pull/9", """{"state":"merged"}""");

        Assert.Equal(actorId.Value, refreshed.AddedById);
    }

    [Fact]
    public async Task RefreshArtifactAsync_DoesNotMatchOtherIssues()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        const string DedupKey = "github:acme/app#7";

        await service.RefreshArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, DedupKey, "PR",
            "https://github.test/acme/app/pull/7");

        await service.RefreshArtifactAsync(
            fixture.OtherIssue.Id, ArtifactKind.PullRequest, DedupKey, "PR",
            "https://github.test/acme/app/pull/7");

        // The dedup key scopes to an issue, so the same PR can be correlated to two issues.
        Assert.Equal(2, await fixture.Db.Artifacts.CountAsync());
    }

    [Theory]
    [InlineData(ArtifactKind.File)]
    [InlineData(ArtifactKind.Link)]
    [InlineData(ArtifactKind.Deployment)]
    public async Task RefreshArtifactAsync_RejectsNonRefreshableKind(ArtifactKind kind)
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.RefreshArtifactAsync(
            fixture.Issue.Id, kind, "dedup", "Title", "https://example.test"));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.ToListAsync());
    }

    [Fact]
    public async Task RefreshArtifactAsync_RejectsEmptyDedupKey()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.RefreshArtifactAsync(
            fixture.Issue.Id, ArtifactKind.PullRequest, "  ", "Title", "https://example.test"));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task RemoveArtifactAsync_DeletesArtifactPurgesContentAndRecordsActivity()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var actorId = MemberId.New();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id,
            ArtifactKind.File,
            "crash.log",
            inlineContent: new ArtifactInlineContent(Encoding.UTF8.GetBytes("log")));

        await service.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), actorId);

        Assert.Empty(await fixture.Db.Artifacts.ToListAsync());
        Assert.DoesNotContain(artifact.ContentReference, fixture.Store.Stored.Keys);

        var removal = await fixture.Db.ActivityEvents
            .SingleAsync(activity => activity.Type == ActivityEventType.ArtifactRemoved);
        Assert.Equal(actorId, removal.ActorId);
    }

    [Fact]
    public async Task RemoveArtifactAsync_RejectsArtifactBelongingToAnotherIssue()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, ArtifactKind.Link, "Title", "https://example.test");

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.RemoveArtifactAsync(
            fixture.OtherIssue.Id, new ArtifactId(artifact.Id)));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
        Assert.Single(await fixture.Db.Artifacts.ToListAsync());
    }

    [Fact]
    public async Task RemoveArtifactAsync_RejectsUnknownArtifact()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<ArtifactException>(() => service.RemoveArtifactAsync(
            fixture.Issue.Id, ArtifactId.New()));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    /// <summary>Records what it was asked to store so tests can assert content never bypasses the store.</summary>
    private sealed class RecordingArtifactStore : IArtifactStore
    {
        public Dictionary<string, ArtifactContent> Stored { get; } = [];

        public string? LastReference { get; private set; }

        public Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default)
        {
            var reference = Guid.NewGuid().ToString("N");
            Stored[reference] = new ArtifactContent(content, contentType);
            LastReference = reference;
            return Task.FromResult(reference);
        }

        public Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default) =>
            Task.FromResult(Stored.GetValueOrDefault(reference));

        public Task DeleteAsync(string reference, CancellationToken ct = default)
        {
            Stored.Remove(reference);
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for an unreachable store so fail-closed behavior can be asserted.</summary>
    private sealed class ThrowingArtifactStore : IArtifactStore
    {
        public Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default) =>
            throw new IOException("store unavailable");

        public Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default) =>
            throw new IOException("store unavailable");

        public Task DeleteAsync(string reference, CancellationToken ct = default) =>
            throw new IOException("store unavailable");
    }

    private sealed class ArtifactFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IArtifactStore store;

        private ArtifactFixture(
            SqliteConnection connection,
            AnvilboardDbContext db,
            IArtifactStore store,
            Issue issue,
            Issue otherIssue,
            Issue orphanedIssue)
        {
            this.connection = connection;
            this.store = store;
            Db = db;
            Issue = issue;
            OtherIssue = otherIssue;
            OrphanedIssue = orphanedIssue;
        }

        public AnvilboardDbContext Db { get; }
        public Issue Issue { get; }
        public Issue OtherIssue { get; }

        /// <summary>An issue whose team row is absent, so it resolves to no workspace.</summary>
        public Issue OrphanedIssue { get; }

        /// <summary>The recording store, for tests that assert on stored content.</summary>
        public RecordingArtifactStore Store => (RecordingArtifactStore)store;

        public ArtifactService CreateService() => new(Db, store);

        public static async Task<ArtifactFixture> CreateAsync(IArtifactStore? store = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);

            // Creates the real schema, so the filtered unique (IssueId, DedupKey) index that makes
            // the refresh upsert safe is genuinely exercised rather than simulated.
            await db.Database.EnsureCreatedAsync();

            var workspaceId = WorkspaceId.New();
            var teamId = TeamId.New();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Test workspace", Slug = "test-workspace", CreatedAt = DateTimeOffset.UtcNow });
            db.Teams.Add(new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Test team", Key = "TST", CreatedAt = DateTimeOffset.UtcNow });

            Issue MakeIssue(string key) => new()
            {
                Id = IssueId.New(),
                TeamId = teamId,
                Key = key,
                Title = $"Issue {key}",
                Status = IssueStatus.Backlog,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var issue = MakeIssue("TST-1");
            var otherIssue = MakeIssue("TST-2");

            // An issue whose team does not exist: nothing resolves it to a workspace, so every
            // artifact operation must refuse it rather than treating it as reachable.
            var orphanedIssue = MakeIssue("ORP-1");
            orphanedIssue.TeamId = TeamId.New();

            db.Issues.AddRange(issue, otherIssue, orphanedIssue);
            await db.SaveChangesAsync();

            return new ArtifactFixture(connection, db, store ?? new RecordingArtifactStore(), issue, otherIssue, orphanedIssue);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
