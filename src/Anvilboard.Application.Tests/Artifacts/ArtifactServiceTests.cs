using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Artifacts;

/// <summary>
/// Covers the attach / list / remove paths of `Anvilboard.Application/Artifacts/ArtifactService.cs`
/// (`docs/plans/artifacts.md` §8.3, §8.5, §15): AC-ART-101, AC-ART-102, AC-ART-103, AC-ART-104,
/// AC-ART-105, and AC-ART-109.
/// </summary>
public sealed class ArtifactServiceTests
{
    [Fact]
    public async Task AttachArtifact_PersistsWithActorProvenanceAndAudit()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Failing CI run", "https://ci.example.test/run/42",
            actorId: fixture.Member.Id);

        Assert.Equal("link", artifact.Kind);
        Assert.Equal("local", artifact.Source);
        Assert.Equal(fixture.Member.Id.Value, artifact.AddedById);

        var stored = await fixture.Db.Artifacts.AsNoTracking().SingleAsync();
        Assert.Equal(ArtifactKind.Link, stored.Kind);
        Assert.Equal(fixture.Issue.Id, stored.IssueId);

        var activity = await fixture.Db.ActivityEvents.AsNoTracking().SingleAsync();
        Assert.Equal(ActivityEventType.ArtifactAttached, activity.Type);

        var audit = await fixture.Db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("ArtifactAttached", audit.Action);
        Assert.Equal("artifact", audit.TargetType);
        Assert.Equal(artifact.Id.ToString(), audit.TargetId);
        Assert.Contains($"kind=link", audit.ResultSummary);

        // The summary is provenance only: leaking the reference would expose private URLs (§11.3).
        Assert.DoesNotContain("ci.example.test", audit.ResultSummary);
    }

    [Fact]
    public async Task AttachArtifact_InvalidKind_RejectsWithValidationFailedAndPersistsNothing()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "screenshot", "A screenshot", "https://example.test/s.png",
            actorId: fixture.Member.Id));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Contains("screenshot", exception.Message);
        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AttachArtifact_UnknownIssue_ReferencedEntityNotFound()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            IssueId.New(), "link", "Anything", "https://example.test", actorId: fixture.Member.Id));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifact_AutomationWithoutSource_IsRejected()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        // BR-ART-1: only an actor-attributed attach may fall back to "local"; automation must name
        // itself, otherwise its output would be indistinguishable from a human action.
        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Agent output", "https://example.test/log"));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifact_AutomationSource_LeavesAddedByNull()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "file", "Agent diff", "blob-ref", source: "agent:automation");

        Assert.Null(artifact.AddedById);
        Assert.Equal("agent:automation", artifact.Source);

        var audit = await fixture.Db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("automation:agent:automation", audit.ActorId);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task AttachArtifact_TitleLengthBoundary(int length, bool shouldSucceed)
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var title = new string('t', length);

        if (shouldSucceed)
        {
            var artifact = await service.AttachArtifactAsync(
                fixture.Issue.Id, "link", title, "https://example.test", actorId: fixture.Member.Id);
            Assert.Equal(title, artifact.Title);
            return;
        }

        // Must be a validation failure, never a truncating or provider-level database error.
        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "link", title, "https://example.test", actorId: fixture.Member.Id));
        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifact_OversizedMetadata_IsRejected()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Big", "https://example.test",
            actorId: fixture.Member.Id, metadata: new string('m', (8 * 1024) + 1)));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task AttachArtifact_WithBytes_StoresContentThenRow()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "file", "Run log", [1, 2, 3], "text/plain", "agent:automation");

        Assert.Equal(1, fixture.Store.StoreCallCount);
        Assert.Empty(fixture.Store.Deleted);

        var content = await fixture.Store.RetrieveAsync(artifact.ContentReference);
        Assert.NotNull(content);
        Assert.Equal([1, 2, 3], content.Bytes);
    }

    [Fact]
    public async Task AttachArtifact_StoreFailure_NoPartialArtifactPersisted()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var throwingStore = new ThrowingArtifactStore();
        var service = fixture.CreateService(throwingStore);

        var exception = await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "file", "Run log", [1, 2, 3], "text/plain", "agent:automation"));

        Assert.Equal("ARTIFACT_STORE_UNAVAILABLE", exception.ErrorCode);
        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ActivityEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AttachArtifact_InvalidKindWithBytes_NeverTouchesTheStore()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<ArtifactException>(() => service.AttachArtifactAsync(
            fixture.Issue.Id, "screenshot", "Run log", [1, 2, 3], "text/plain", "agent:automation"));

        // Validation precedes the store call, so a rejected attach cannot orphan a blob.
        Assert.Equal(0, fixture.Store.StoreCallCount);
    }

    [Fact]
    public async Task ListArtifacts_OrdersByCreatedAtThenId()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        for (var i = 0; i < 3; i++)
        {
            await service.AttachArtifactAsync(
                fixture.Issue.Id, "link", $"Artifact {i}", $"https://example.test/{i}",
                actorId: fixture.Member.Id);
        }

        // Force a CreatedAt collision so the Id tie-break is the only thing keeping the order stable.
        var shared = DateTimeOffset.UtcNow;
        foreach (var artifact in await fixture.Db.Artifacts.ToListAsync())
        {
            artifact.UpdatedAt = shared;
            fixture.Db.Entry(artifact).Property(a => a.CreatedAt).CurrentValue = shared;
        }

        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var listed = await service.ListArtifactsAsync(fixture.Issue.Id);

        Assert.Equal(3, listed.Count);
        Assert.Equal(listed.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Select(a => a.Id), listed.Select(a => a.Id));
        Assert.Equal(await service.ListArtifactsAsync(fixture.Issue.Id), listed);
    }

    [Fact]
    public async Task ListArtifacts_ScopedToTheIssue()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AttachArtifactAsync(fixture.Issue.Id, "link", "Mine", "https://example.test/a", actorId: fixture.Member.Id);
        await service.AttachArtifactAsync(fixture.OtherIssue.Id, "link", "Theirs", "https://example.test/b", actorId: fixture.Member.Id);

        var listed = await service.ListArtifactsAsync(fixture.Issue.Id);

        Assert.Equal("Mine", Assert.Single(listed).Title);
    }

    [Fact]
    public async Task ListArtifacts_EmptyIssue_ReturnsEmptyList()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();

        Assert.Empty(await service.ListArtifactsAsync(fixture.Issue.Id));
    }

    [Fact]
    public async Task RemoveArtifact_AuditedAndBlobPurged()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "file", "Run log", [9], "text/plain", "agent:automation");
        fixture.Db.ChangeTracker.Clear();

        await service.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), fixture.Member.Id);

        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
        Assert.Equal([artifact.ContentReference], fixture.Store.Deleted);

        var removal = await fixture.Db.ActivityEvents.AsNoTracking()
            .SingleAsync(activity => activity.Type == ActivityEventType.ArtifactRemoved);
        Assert.Equal(fixture.Issue.Id, removal.IssueId);

        Assert.Contains(
            await fixture.Db.AuditEvents.AsNoTracking().ToListAsync(),
            audit => audit.Action == "ArtifactRemoved" && audit.TargetId == artifact.Id.ToString());
    }

    [Fact]
    public async Task RemoveArtifact_WrongIssueScope_NotFound()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var artifact = await service.AttachArtifactAsync(
            fixture.OtherIssue.Id, "link", "Theirs", "https://example.test/b", actorId: fixture.Member.Id);
        fixture.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<ArtifactException>(() =>
            service.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), fixture.Member.Id));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
        Assert.Single(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
        Assert.Empty(fixture.Store.Deleted);
    }

    [Fact]
    public async Task RemoveArtifact_Twice_SecondCallIsNotFound()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Gone", "https://example.test/x", actorId: fixture.Member.Id);
        fixture.Db.ChangeTracker.Clear();

        await service.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), fixture.Member.Id);

        // Explicitly not idempotent: silently succeeding would hide a caller working from stale state.
        var exception = await Assert.ThrowsAsync<ArtifactException>(() =>
            service.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), fixture.Member.Id));
        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", exception.ErrorCode);
    }

    [Fact]
    public async Task RemoveArtifact_PurgeFailure_StillRemovesTheRow()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var artifact = await service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Gone", "https://example.test/x", actorId: fixture.Member.Id);
        fixture.Db.ChangeTracker.Clear();

        // BR-ART-7: a dangling blob is reclaimable space; a dangling row is visible corruption, so
        // the purge is best-effort and never fails the removal.
        var failing = fixture.CreateService(new ThrowingArtifactStore());
        await failing.RemoveArtifactAsync(fixture.Issue.Id, new ArtifactId(artifact.Id), fixture.Member.Id);

        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
    }
}
