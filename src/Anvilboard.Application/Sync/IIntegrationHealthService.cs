using Anvilboard.Domain;

namespace Anvilboard.Application.Sync;

/// <summary>
/// Writes and reads the per-integration sync telemetry that answers "is this board's data fresh?".
/// </summary>
/// <remarks>
/// <para>
/// This interface owns the <b>single</b> implementation of the freshness derivation
/// (<see cref="DeriveCondition"/>). The board query's <c>syncCondition</c> filter and the
/// dashboard's freshness summary both route through it rather than re-deriving, because a
/// dashboard that disagreed with the board filter is exactly the drift tech-design §7.5 forbids.
/// </para>
/// <para>
/// Nothing here accepts or returns credential material: <see cref="IntegrationHealthDto"/> carries
/// only timings, a coarse error category, and a counter.
/// </para>
/// </remarks>
public interface IIntegrationHealthService
{
    /// <summary>
    /// Upserts the health row for one integration from a completed sync attempt, and records an
    /// audit event if — and only if — the derived condition crossed into or out of
    /// <see cref="SyncCondition.Failed"/>.
    /// </summary>
    Task RecordAttemptAsync(SyncAttemptOutcome outcome, CancellationToken ct = default);

    /// <summary>Returns every integration's current health for one workspace.</summary>
    Task<IReadOnlyList<IntegrationHealthDto>> GetHealthAsync(
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct providers whose integrations are currently in
    /// <paramref name="condition"/> within <paramref name="workspaceId"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IntegrationProvider.Local"/> can never appear: locally created issues have no
    /// <see cref="Integration"/> row and therefore no sync health to be fresh or stale about.
    /// </remarks>
    Task<IReadOnlySet<IntegrationProvider>> ProvidersInConditionAsync(
        WorkspaceId workspaceId,
        SyncCondition condition,
        CancellationToken ct = default);

    /// <summary>
    /// The freshness derivation (tech-design §7.5), evaluated top-down. Pure and static so it is
    /// testable without a database and so every call site is greppable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SyncCondition.Failed"/> is deliberately evaluated <b>above</b>
    /// <see cref="SyncCondition.Paused"/>: pausing a broken integration must not hide that it is
    /// broken, or "pause" becomes a tool for concealing an outage rather than for stopping traffic.
    /// </para>
    /// </remarks>
    /// <param name="status">The owning integration's lifecycle status.</param>
    /// <param name="lastAttemptAt">When the last sync attempt ran, or null if none ever has.</param>
    /// <param name="lastSuccessAt">When the last successful sync ran, or null if none ever has.</param>
    /// <param name="lastErrorCategory">The category of the last failure, or null if the last attempt succeeded.</param>
    /// <param name="stalenessThreshold">How long without a success counts as stale. Must not be negative.</param>
    /// <param name="now">The evaluation instant.</param>
    static SyncCondition DeriveCondition(
        IntegrationStatus status,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? lastSuccessAt,
        SyncErrorCategory? lastErrorCategory,
        TimeSpan stalenessThreshold,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stalenessThreshold, TimeSpan.Zero);

        if (lastErrorCategory is not null && (lastSuccessAt is null || lastSuccessAt < lastAttemptAt))
        {
            return SyncCondition.Failed;
        }

        if (status == IntegrationStatus.Paused)
        {
            return SyncCondition.Paused;
        }

        if (lastSuccessAt is not { } success || success < now - stalenessThreshold)
        {
            return SyncCondition.Stale;
        }

        return SyncCondition.Fresh;
    }
}

/// <summary>
/// The result of one completed sync attempt against one integration, as reported by the sync
/// coordinator.
/// </summary>
/// <param name="IntegrationId">The integration the attempt is attributed to.</param>
/// <param name="WorkspaceId">The integration's owning workspace, denormalized onto the health row.</param>
/// <param name="PluginKey">The plugin whose loop produced the attempt.</param>
/// <param name="AttemptedAt">When the attempt started.</param>
/// <param name="Succeeded">Whether the attempt completed without throwing.</param>
/// <param name="ErrorCategory">The coarse failure category; must be null when <paramref name="Succeeded"/> is true.</param>
/// <param name="NextAttemptNotBefore">The computed backoff floor, or null to clear it.</param>
/// <param name="CursorToken">The opaque plugin cursor reached by a successful run.</param>
public sealed record SyncAttemptOutcome(
    IntegrationId IntegrationId,
    WorkspaceId WorkspaceId,
    string PluginKey,
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    SyncErrorCategory? ErrorCategory = null,
    DateTimeOffset? NextAttemptNotBefore = null,
    string? CursorToken = null);

/// <summary>
/// Safe health representation. Carries no credential material and no provider error message —
/// only the coarse <see cref="LastErrorCategory"/> — so it is readable by any actor holding
/// <see cref="Permission.ReadIntegrationHealth"/>, which grants no access to integration secrets.
/// </summary>
public sealed record IntegrationHealthDto(
    Guid IntegrationId,
    IntegrationProvider Provider,
    string PluginKey,
    IntegrationStatus Status,
    SyncCondition Condition,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    SyncErrorCategory? LastErrorCategory,
    int ConsecutiveFailureCount,
    DateTimeOffset? NextAttemptNotBefore);
