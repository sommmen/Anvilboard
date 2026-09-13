namespace Anvilboard.Domain;

/// <summary>
/// Why a sync attempt failed, coarse enough that a third-party plugin throwing an arbitrary
/// exception still lands somewhere sensible. The category — never the exception message — is what
/// gets persisted and surfaced, so a provider error string can never leak a credential into the
/// health surface.
/// </summary>
public enum SyncErrorCategory
{
    /// <summary>Credentials were rejected (401, or 403 without a rate-limit signal). Non-transient.</summary>
    Auth = 0,

    /// <summary>The provider asked us to slow down (429, or a rate-limit-flavoured 403).</summary>
    RateLimited = 1,

    /// <summary>Network, DNS, TLS, timeout, or 5xx. Transient by default.</summary>
    Transport = 2,

    /// <summary>Anything a plugin threw that does not map to the above.</summary>
    Unknown = 3,
}

/// <summary>
/// The derived freshness verdict for one integration. Never stored — always computed from
/// <see cref="IntegrationHealth"/> plus <see cref="Integration.Status"/> by the single
/// implementation in <c>IIntegrationHealthService.DeriveCondition</c>, so the board filter and the
/// dashboard summary can never disagree (tech-design §7.5).
/// </summary>
public enum SyncCondition
{
    Fresh = 0,
    Stale = 1,
    Paused = 2,
    Failed = 3,
}

/// <summary>
/// Machine-written sync telemetry for exactly one <see cref="Integration"/>: when it was last
/// attempted, when it last succeeded, why it last failed, and when the coordinator is next allowed
/// to try again.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate aggregate rather than columns on <see cref="Integration"/>
/// (<c>docs/plans/integration-sync-health.md</c> DR-ISH-001): <see cref="Integration"/> is
/// administrator-authored configuration that holds
/// <see cref="Integration.ProtectedCredentials"/>, whereas this is high-churn machine telemetry
/// that must be safe to read with a permission that grants no access to secrets. Keeping them
/// apart makes "health carries no credential material" a schema fact instead of a review rule —
/// <b>no credential-bearing property may be added to this type</b>.
/// </para>
/// <para>
/// <see cref="WorkspaceId"/> is denormalized from the owning <see cref="Integration"/> so a scoped
/// read needs no join; the two can never diverge because an integration never changes workspace.
/// </para>
/// </remarks>
public sealed class IntegrationHealth
{
    /// <summary>Consecutive-failure count is clamped here; 2^16 × any sane poll interval already
    /// exceeds every configured backoff ceiling, and clamping keeps the shift in
    /// <c>SyncCoordinator</c> from overflowing.</summary>
    public const int MaxConsecutiveFailureCount = 16;

    public IntegrationHealthId Id { get; init; }

    public required IntegrationId IntegrationId { get; init; }

    /// <summary>Denormalized from the owning integration so scoped reads need no join.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>
    /// The <see cref="Plugins.Abstractions.PluginManifest.Key"/>-equivalent string that produced
    /// this row, which is what disambiguates two <see cref="IntegrationProvider.Custom"/>
    /// integrations from each other.
    /// </summary>
    public required string PluginKey { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Null once a later attempt succeeds; a non-null value with no newer success is what
    /// makes the integration <see cref="SyncCondition.Failed"/>.</summary>
    public SyncErrorCategory? LastErrorCategory { get; set; }

    public int ConsecutiveFailureCount { get; set; }

    /// <summary>The backoff floor: the coordinator must not re-attempt before this instant.</summary>
    public DateTimeOffset? NextAttemptNotBefore { get; set; }

    /// <summary>Opaque plugin cursor from the last successful run — freshness evidence for
    /// <c>FR-INT-002 AC5</c>, never interpreted by the host.</summary>
    public string? LastCursorToken { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
