using Anvilboard.Application.Artifacts;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Artifacts;

/// <summary>
/// Covers the automation/enrichment provenance contract (`docs/plans/artifacts.md` §3.5 US-A2,
/// FR-ART-002): AC-ART-106, AC-ART-107, and AC-ART-108. There is no enrichment hook host yet, so
/// these drive <see cref="ArtifactService"/> the way such a hook would — through the same public
/// contract, with no actor and an explicit hook source key.
/// </summary>
public sealed class ArtifactExpansionTests
{
    private const string HookSource = "slack-thread-expansion";
    private const string ThreadUrl = "https://slack.test/archives/C1/p1700000000";

    [Fact]
    public async Task SlackThreadExpansion_AttachesWithAutomatedProvenance()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var hook = new FakeExpansionHook(fixture.CreateService());

        var artifact = await hook.ExpandAsync(fixture.Issue.Id, ThreadUrl, "Slack thread: incident 14");

        // AC-ART-106: AddedById == null is the sole provenance discriminator (BR-ART-2), which is
        // what lets the UI distinguish automation output from a human attachment.
        Assert.Null(artifact.AddedById);
        Assert.Equal(HookSource, artifact.Source);

        var audit = await fixture.Db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("ArtifactAttached", audit.Action);
        Assert.Equal($"automation:{HookSource}", audit.ActorId);
    }

    [Fact]
    public async Task PartialFetchFailure_NoArtifactPersisted()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var hook = new FakeExpansionHook(fixture.CreateService()) { FailFetch = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hook.ExpandAsync(fixture.Issue.Id, ThreadUrl, "Slack thread: incident 14"));

        // AC-ART-107: a half-finished expansion leaves nothing behind for a user to puzzle over.
        Assert.Empty(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ActivityEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RepeatExpansion_UpdatesExistingArtifactIdempotently()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var hook = new FakeExpansionHook(fixture.CreateService());

        await hook.ExpandAsync(fixture.Issue.Id, ThreadUrl, "Slack thread: incident 14");
        var second = await hook.ExpandAsync(fixture.Issue.Id, ThreadUrl, "Slack thread: incident 14 (resolved)");

        // AC-ART-108: re-expanding the same source URL must not accumulate rows. Row identity is
        // not preserved here because BR-ART-3 restricts the dedup-key upsert to `pull_request`; a
        // `link` hook can only converge by replacing. Widening refresh to link kinds would preserve
        // the identity too, and is the open question this test pins the current behaviour against.
        Assert.Single(await fixture.Db.Artifacts.AsNoTracking().ToListAsync());
        Assert.Equal("Slack thread: incident 14 (resolved)", second.Title);
        Assert.Equal(ThreadUrl, second.ContentReference);
    }

    [Fact]
    public async Task Expansion_ArtifactsAreListedAlongsideManualOnes()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var service = fixture.CreateService();
        var hook = new FakeExpansionHook(service);

        await service.AttachArtifactAsync(
            fixture.Issue.Id, "link", "Manual link", "https://example.test/manual", actorId: fixture.Member.Id);
        await hook.ExpandAsync(fixture.Issue.Id, ThreadUrl, "Slack thread");

        var listed = await service.ListArtifactsAsync(fixture.Issue.Id);

        Assert.Equal(2, listed.Count);
        Assert.Single(listed, artifact => artifact.AddedById is null);
        Assert.Single(listed, artifact => artifact.AddedById == fixture.Member.Id.Value);
    }

    /// <summary>
    /// Stands in for a real enrichment hook: it fetches (here, trivially), then attaches through the
    /// public service contract. Repeat expansions of the same URL find and update the existing row,
    /// which is how a hook keeps itself idempotent without a dedicated dedup key on a link artifact.
    /// </summary>
    private sealed class FakeExpansionHook(ArtifactService artifacts)
    {
        public bool FailFetch { get; init; }

        public async Task<ArtifactDto> ExpandAsync(IssueId issueId, string sourceUrl, string title)
        {
            if (FailFetch)
            {
                throw new InvalidOperationException("the external fetch failed partway through");
            }

            var existing = (await artifacts.ListArtifactsAsync(issueId))
                .FirstOrDefault(artifact => artifact.Source == HookSource && artifact.ContentReference == sourceUrl);
            if (existing is not null)
            {
                await artifacts.RemoveArtifactAsync(issueId, new ArtifactId(existing.Id));
            }

            return await artifacts.AttachArtifactAsync(
                issueId, "link", title, sourceUrl, source: HookSource);
        }
    }
}
