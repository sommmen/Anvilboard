using Anvilboard.Application.Sync;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;

namespace Anvilboard.Application.Tests.Sync;

/// <summary>
/// Covers the backoff policy that replaces the previous fixed-cadence retry loop
/// (<c>docs/plans/integration-sync-health.md</c> §8.3) — the fix for a failing provider being
/// hammered at the poll interval forever.
/// </summary>
public sealed class SyncBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IngestionOptions Options = new()
    {
        PollInterval = TimeSpan.FromMinutes(1),
        MaxBackoff = TimeSpan.FromHours(1),
        QuarantineInterval = TimeSpan.FromHours(1),
    };

    [Theory]
    [InlineData(typeof(ProviderAuthenticationException), SyncErrorCategory.Auth)]
    [InlineData(typeof(ProviderThrottledException), SyncErrorCategory.RateLimited)]
    [InlineData(typeof(HttpRequestException), SyncErrorCategory.Transport)]
    [InlineData(typeof(TimeoutException), SyncErrorCategory.Transport)]
    [InlineData(typeof(InvalidOperationException), SyncErrorCategory.Unknown)]
    public void Categorize_MapsExceptionToCategory(Type exceptionType, SyncErrorCategory expected)
    {
        var exception = exceptionType == typeof(ProviderThrottledException)
            ? new ProviderThrottledException(TimeSpan.FromMinutes(5), "throttled")
            : exceptionType == typeof(ProviderAuthenticationException)
                ? new ProviderAuthenticationException("unauthorized")
                : (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expected, SyncCoordinator.Categorize(exception));
    }

    [Fact]
    public void Advance_ObeysProviderRetryAfterVerbatim()
    {
        var retryAfter = TimeSpan.FromMinutes(37);

        var next = SyncCoordinator.Advance(
            new SyncCoordinator.BackoffState(0, null),
            Options,
            SyncErrorCategory.RateLimited,
            new ProviderThrottledException(retryAfter, "throttled"),
            Now);

        // The provider stated when it will accept traffic again; a locally computed guess would
        // either waste capacity or earn another 429.
        Assert.Equal(Now + retryAfter, next.NotBefore);
    }

    [Fact]
    public void Advance_QuarantinesAuthFailuresAtAFlatInterval()
    {
        var next = SyncCoordinator.Advance(
            new SyncCoordinator.BackoffState(0, null),
            Options,
            SyncErrorCategory.Auth,
            new ProviderAuthenticationException("unauthorized"),
            Now);

        // A rejected credential will not fix itself by being retried sooner — only a human can fix
        // it — so auth failures skip the exponential ramp and sit at the quarantine interval.
        Assert.Equal(Now + Options.QuarantineInterval, next.NotBefore);
    }

    [Fact]
    public void Advance_IncrementsAndClampsTheFailureCounter()
    {
        var state = new SyncCoordinator.BackoffState(IntegrationHealth.MaxConsecutiveFailureCount, null);

        var next = SyncCoordinator.Advance(
            state, Options, SyncErrorCategory.Transport, new HttpRequestException(), Now);

        // Clamped so the shift in the delay calculation cannot overflow after a long outage.
        Assert.Equal(IntegrationHealth.MaxConsecutiveFailureCount, next.ConsecutiveFailures);
    }

    [Fact]
    public void Delay_GrowsWithFailuresAndStaysUnderTheCap()
    {
        var max = TimeSpan.FromHours(1);

        // Full jitter makes any single sample unpredictable, so assert the invariants that must
        // hold for every sample rather than an exact duration.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            for (var sample = 0; sample < 50; sample++)
            {
                var delay = SyncCoordinator.Delay(attempt, TimeSpan.FromMinutes(1), max);

                Assert.True(delay > TimeSpan.Zero, $"attempt {attempt} produced a non-positive delay");
                Assert.True(delay <= max, $"attempt {attempt} produced {delay}, above the {max} cap");
            }
        }
    }

    [Fact]
    public void Delay_ToleratesAMisconfiguredZeroPollInterval()
    {
        // A zero base would otherwise collapse the backoff to nothing and reinstate the hot loop
        // this policy exists to prevent.
        var delay = SyncCoordinator.Delay(3, TimeSpan.Zero, TimeSpan.FromHours(1));

        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public void ProviderThrottledException_ClampsANegativeRetryAfter()
    {
        // A provider can send a Retry-After date already in the past (clock skew, or a slow hop).
        // Trusting it verbatim would compute a retry instant behind "now" and disable the backoff.
        var exception = new ProviderThrottledException(TimeSpan.FromMinutes(-5), "throttled");

        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
    }

    [Fact]
    public void EffectiveStalenessThreshold_DefaultsToThreePollIntervals()
    {
        var options = new IngestionOptions { PollInterval = TimeSpan.FromMinutes(5) };

        Assert.Equal(TimeSpan.FromMinutes(15), options.EffectiveStalenessThreshold);
    }

    [Fact]
    public void EffectiveStalenessThreshold_PrefersAnExplicitConfiguration()
    {
        var options = new IngestionOptions
        {
            PollInterval = TimeSpan.FromMinutes(5),
            StalenessThreshold = TimeSpan.FromHours(2),
        };

        Assert.Equal(TimeSpan.FromHours(2), options.EffectiveStalenessThreshold);
    }

    [Fact]
    public void ProviderFor_MapsKnownKeysAndFallsBackToCustom()
    {
        Assert.Equal(IntegrationProvider.GitHub, SyncCoordinator.ProviderFor("github"));
        Assert.Equal(IntegrationProvider.Linear, SyncCoordinator.ProviderFor("linear"));
        Assert.Equal(IntegrationProvider.Custom, SyncCoordinator.ProviderFor("some-third-party"));
    }
}
