using Anvilboard.Application.Sync;
using Anvilboard.Domain;

namespace Anvilboard.Application.Tests.Sync;

/// <summary>
/// Covers the single freshness derivation every consumer routes through
/// (<c>docs/plans/integration-sync-health.md</c> §7.5). These are pure-function tests: if the
/// precedence here is wrong, the board filter and the dashboard are wrong together and silently.
/// </summary>
public sealed class DeriveConditionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    private static SyncCondition Derive(
        IntegrationStatus status,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? lastSuccessAt,
        SyncErrorCategory? category) =>
        IIntegrationHealthService.DeriveCondition(
            status, lastAttemptAt, lastSuccessAt, category, Threshold, Now);

    [Fact]
    public void RecentSuccessWithNoError_IsFresh() =>
        Assert.Equal(
            SyncCondition.Fresh,
            Derive(IntegrationStatus.Enabled, Now.AddMinutes(-1), Now.AddMinutes(-1), null));

    [Fact]
    public void SuccessOlderThanThreshold_IsStale() =>
        Assert.Equal(
            SyncCondition.Stale,
            Derive(IntegrationStatus.Enabled, Now.AddMinutes(-20), Now.AddMinutes(-20), null));

    [Fact]
    public void NeverSynced_IsStaleRatherThanFresh() =>
        Assert.Equal(
            SyncCondition.Stale,
            Derive(IntegrationStatus.Enabled, null, null, null));

    [Fact]
    public void PausedWithHealthyHistory_IsPaused() =>
        Assert.Equal(
            SyncCondition.Paused,
            Derive(IntegrationStatus.Paused, Now.AddMinutes(-1), Now.AddMinutes(-1), null));

    [Fact]
    public void PausedLongAgo_IsPausedNotStale() =>
        // Pausing stops polling, so a paused integration inevitably ages past its staleness
        // threshold. Reporting that as "stale" would raise an alarm about a deliberate act.
        Assert.Equal(
            SyncCondition.Paused,
            Derive(IntegrationStatus.Paused, Now.AddDays(-30), Now.AddDays(-30), null));

    [Fact]
    public void FailedAttemptAfterLastSuccess_IsFailed() =>
        Assert.Equal(
            SyncCondition.Failed,
            Derive(IntegrationStatus.Enabled, Now.AddMinutes(-1), Now.AddMinutes(-5), SyncErrorCategory.Transport));

    [Fact]
    public void PausingABrokenIntegration_StillReportsFailed() =>
        // The precedence that matters most: if Paused outranked Failed, an operator could silence a
        // broken integration by pausing it and the dashboard would stop reporting the breakage.
        Assert.Equal(
            SyncCondition.Failed,
            Derive(IntegrationStatus.Paused, Now.AddMinutes(-1), Now.AddMinutes(-5), SyncErrorCategory.Auth));

    [Fact]
    public void SuccessAfterAFailure_ClearsFailed() =>
        // Defensive: a stale LastErrorCategory that outlived its failure must not pin the
        // integration to Failed once a later attempt has succeeded.
        Assert.Equal(
            SyncCondition.Fresh,
            Derive(IntegrationStatus.Enabled, Now.AddMinutes(-1), Now.AddMinutes(-1), SyncErrorCategory.Transport));

    [Fact]
    public void NegativeThreshold_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IIntegrationHealthService.DeriveCondition(
            IntegrationStatus.Enabled, Now, Now, null, TimeSpan.FromMinutes(-1), Now));
}
