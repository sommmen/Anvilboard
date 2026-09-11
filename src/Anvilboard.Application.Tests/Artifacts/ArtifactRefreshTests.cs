using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Artifacts;

/// <summary>
/// Covers the idempotent upsert path (`docs/features/artifacts.md`, RefreshArtifactAsync): AC-ART-110, AC-ART-111, the
/// `pull_request`-only restriction (BR-ART-3), and convergence when two provider deliveries race
/// for the same dedup key.
/// </summary>
public sealed class ArtifactRefreshTests
{
    private const string PrUrl = "https://github.test/org/repo/pull/7";

    [Fact]
    public async Task Refresh_FirstEvent_AttachesNewArtifact()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix the widget", PrUrl, "github",
            metadata: """{"number":7,"state":"open","checksStatus":"pending"}""");

        Assert.Equal("pull_request", artifact.Kind);
        Assert.Equal("github", artifact.Source);
        Assert.Equal("github:org/repo#7", artifact.DedupKey);
        Assert.Null(artifact.AddedById);

        var activity = await fixture.Db.ActivityEvents.AsNoTracking().SingleAsync();
        Assert.Equal(ActivityEventType.ArtifactAttached, activity.Type);

        var audit = await fixture.Db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("ArtifactAttached", audit.Action);
    }

    [Fact]
    public async Task Refresh_SubsequentEvent_UpdatesInPlace()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var first = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix the widget", PrUrl, "github",
            metadata: """{"state":"open"}""");

        var second = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix the widget (merged)", PrUrl, "github",
            metadata: """{"state":"merged"}""");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Fix the widget (merged)", second.Title);
        Assert.Equal("""{"state":"merged"}""", second.Metadata);
        Assert.Equal(first.CreatedAt, second.CreatedAt);

        Assert.Single(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());

        var activityTypes = await fixture.Db.ActivityEvents.AsNoTracking().Select(a => a.Type).ToListAsync();
        Assert.Equal([ActivityEventType.ArtifactAttached, ActivityEventType.ArtifactRefreshed], activityTypes);

        var auditActions = await fixture.Db.AuditEvents.AsNoTracking().Select(a => a.Action).ToListAsync();
        Assert.Equal(["ArtifactAttached", "ArtifactRefreshed"], auditActions);
    }

    [Fact]
    public async Task Refresh_ManyEvents_NeverAccumulatesDuplicateRows()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        for (var i = 0; i < 6; i++)
        {
            await service.RefreshArtifactAsync(
                fixture.Issue.Id, "pull_request", "github:org/repo#7", $"Fix the widget ({i})", PrUrl, "github");
        }

        Assert.Single(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Refresh_IdenticalPayload_StillBumpsUpdatedAtAndEmitsRefreshed()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var first = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix the widget", PrUrl, "github");
        await Task.Delay(5);
        var second = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix the widget", PrUrl, "github");

        // Idempotent, not a no-op: the board should still be able to show the provider was heard from.
        Assert.True(second.UpdatedAt > first.UpdatedAt);
        Assert.Contains(
            await fixture.Db.ActivityEvents.AsNoTracking().ToListAsync(),
            activity => activity.Type == ActivityEventType.ArtifactRefreshed);
    }

    [Fact]
    public async Task Refresh_DedupKeyIsScopedPerIssue()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Fix", PrUrl, "github");
        await service.RefreshArtifactAsync(
            fixture.OtherIssue.Id, "pull_request", "github:org/repo#7", "Fix", PrUrl, "github");

        // One PR can legitimately reference two issues; the unique index is (IssueId, DedupKey).
        Assert.Equal(2, await fixture.Db.Artifacts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Refresh_LosesInsertRace_ConvergesOnTheWinningRow()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        // Simulate the competing delivery by writing the winner through a second DbContext on the
        // same connection, so the service's insert hits the live (IssueId, DedupKey) unique index
        // exactly as it would under a genuine concurrent webhook — the service's own lookup already
        // ran and found nothing.
        var winnerId = fixture.RaceInCompetingArtifactOnNextSave(new Artifact
        {
            Id = ArtifactId.New(),
            IssueId = fixture.Issue.Id,
            Kind = ArtifactKind.PullRequest,
            Title = "Raced in first",
            ContentReference = PrUrl,
            Source = "github",
            DedupKey = "github:org/repo#7",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var refreshed = await service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "github:org/repo#7", "Merged", PrUrl, "github");

        Assert.Equal(winnerId.Value, refreshed.Id);
        Assert.Equal("Merged", refreshed.Title);
        Assert.Single(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("link")]
    [InlineData("deployment")]
    public async Task Refresh_NonRefreshableKind_IsRejected(string kind)
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.RefreshArtifactAsync(
            fixture.Issue.Id, kind, "some-key", "Title", "https://example.test", "github"));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Refresh_UnknownIssue_ReferencedEntityNotFound()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.RefreshArtifactAsync(
            IssueId.New(), "pull_request", "github:org/repo#7", "Title", PrUrl, "github"));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task Refresh_EmptyDedupKey_IsRejected()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.RefreshArtifactAsync(
            fixture.Issue.Id, "pull_request", "   ", "Title", PrUrl, "github"));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }
}
