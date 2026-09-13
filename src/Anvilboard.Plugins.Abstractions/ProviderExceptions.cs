namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Base type for a failure an <see cref="IIngestionSource"/> encountered while talking to its
/// remote provider.
/// </summary>
/// <remarks>
/// <para>
/// Plugins translate provider-specific transport details (HTTP status codes, <c>Retry-After</c>
/// headers, GraphQL error extensions) into this small family so the host's sync coordinator can
/// choose a backoff strategy without knowing anything about HTTP — tech-design §7.6 assigns header
/// handling to the adapter, and this keeps <c>Anvilboard.Application</c> free of those semantics.
/// </para>
/// <para>
/// Throwing these is optional. A plugin that throws a plain exception still works: the coordinator
/// categorizes it as transport-or-unknown and applies exponential backoff, which is the correct
/// conservative default.
/// </para>
/// </remarks>
public class ProviderSyncException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The provider asked the caller to slow down and told it for how long (HTTP 429 with
/// <c>Retry-After</c>, or a rate-limit-flavoured 403 with <c>X-RateLimit-Reset</c>).
/// </summary>
/// <remarks>
/// The coordinator honours <see cref="RetryAfter"/> as a floor rather than applying its own
/// exponential delay: a provider that states when it will accept traffic again is more accurate
/// than any local guess, and ignoring it is how an integration gets a credential banned.
/// </remarks>
public sealed class ProviderThrottledException(
    TimeSpan retryAfter,
    string message,
    Exception? innerException = null)
    : ProviderSyncException(message, innerException)
{
    /// <summary>How long the provider asked the caller to wait. Never negative.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
}

/// <summary>
/// The provider rejected the plugin's credentials (HTTP 401, or 403 with no rate-limit signal).
/// </summary>
/// <remarks>
/// Treated as non-transient: retrying a revoked token on the normal cadence never succeeds and
/// only burns the provider's rate budget, so the coordinator quarantines the source on a long,
/// flat interval until an administrator reconfigures it.
/// </remarks>
public sealed class ProviderAuthenticationException(string message, Exception? innerException = null)
    : ProviderSyncException(message, innerException);
