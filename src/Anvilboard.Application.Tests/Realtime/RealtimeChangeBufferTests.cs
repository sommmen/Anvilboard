using Anvilboard.Application.Realtime;
using Anvilboard.Domain;

namespace Anvilboard.Application.Tests.Realtime;

/// <summary>
/// Covers TC-RT-003/TC-RT-004: a burst of changes to one issue must collapse to a bounded number of
/// sends, and a saturated buffer must drop deterministically rather than grow without bound.
/// </summary>
public sealed class RealtimeChangeBufferTests
{
    private static readonly WorkspaceId Workspace = WorkspaceId.New();

    [Fact]
    public void Offer_BurstForSameIssue_CoalescesToLatestVersion()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);
        var issueId = IssueId.New();

        Assert.Equal(RealtimeBufferResult.Buffered, buffer.Offer(IssueChange(issueId, version: 1)));
        Assert.Equal(RealtimeBufferResult.Coalesced, buffer.Offer(IssueChange(issueId, version: 2)));
        Assert.Equal(RealtimeBufferResult.Coalesced, buffer.Offer(IssueChange(issueId, version: 3)));

        var drained = Assert.Single(buffer.Drain().OfType<RealtimeIssueChange>());
        Assert.Equal(3, drained.Version);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Offer_OutOfOrderVersions_KeepsHighestVersion()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);
        var issueId = IssueId.New();

        buffer.Offer(IssueChange(issueId, version: 7));
        buffer.Offer(IssueChange(issueId, version: 4));

        var drained = Assert.Single(buffer.Drain().OfType<RealtimeIssueChange>());
        Assert.Equal(7, drained.Version);
    }

    [Fact]
    public void Offer_CreatedThenUpdated_StaysCreatedSoClientsDoNotPatchAnUnknownIssue()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);
        var issueId = IssueId.New();

        buffer.Offer(IssueChange(issueId, version: 0, RealtimeIssueChangeKind.Created));
        buffer.Offer(IssueChange(issueId, version: 1));

        var drained = Assert.Single(buffer.Drain().OfType<RealtimeIssueChange>());
        Assert.Equal(RealtimeIssueChangeKind.Created, drained.ChangeKind);
        Assert.Equal(1, drained.Version);
    }

    [Fact]
    public void Offer_DistinctIssues_AreNotCoalescedTogether()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);

        buffer.Offer(IssueChange(IssueId.New(), version: 1));
        buffer.Offer(IssueChange(IssueId.New(), version: 1));

        Assert.Equal(2, buffer.Drain().Count);
    }

    [Fact]
    public void Offer_SameIssueInDifferentWorkspaces_AreNotCoalescedTogether()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);
        var issueId = IssueId.New();

        buffer.Offer(IssueChange(issueId, version: 1));
        buffer.Offer(new RealtimeIssueChange(
            WorkspaceId.New(), issueId, 1, RealtimeIssueChangeKind.Updated, "c", DateTimeOffset.UtcNow));

        Assert.Equal(2, buffer.Drain().Count);
    }

    [Fact]
    public void Offer_BeyondCapacity_DropsNewKeysButStillAcceptsPendingOnes()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 2);
        var firstIssue = IssueId.New();

        Assert.Equal(RealtimeBufferResult.Buffered, buffer.Offer(IssueChange(firstIssue, version: 1)));
        Assert.Equal(RealtimeBufferResult.Buffered, buffer.Offer(IssueChange(IssueId.New(), version: 1)));
        Assert.Equal(RealtimeBufferResult.Dropped, buffer.Offer(IssueChange(IssueId.New(), version: 1)));

        // A saturated buffer must never reject an update to something it is already tracking,
        // otherwise a hot issue would be permanently starved of updates.
        Assert.Equal(RealtimeBufferResult.Coalesced, buffer.Offer(IssueChange(firstIssue, version: 2)));
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Offer_DashboardChanges_CoalescePerWorkspace()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 16);

        buffer.Offer(new RealtimeDashboardChange(Workspace, "v1", "c", DateTimeOffset.UtcNow));
        buffer.Offer(new RealtimeDashboardChange(Workspace, "v2", "c", DateTimeOffset.UtcNow.AddMilliseconds(5)));

        var drained = Assert.Single(buffer.Drain().OfType<RealtimeDashboardChange>());
        Assert.Equal("v2", drained.SummaryVersion);
    }

    [Fact]
    public void Drain_EmptiesTheBuffer()
    {
        var buffer = new RealtimeChangeBuffer(capacity: 4);
        buffer.Offer(IssueChange(IssueId.New(), version: 1));

        Assert.Single(buffer.Drain());
        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RealtimeChangeBuffer(capacity: 0));

    private static RealtimeIssueChange IssueChange(
        IssueId issueId,
        long version,
        RealtimeIssueChangeKind kind = RealtimeIssueChangeKind.Updated) =>
        new(Workspace, issueId, version, kind, "correlation", DateTimeOffset.UtcNow);
}
