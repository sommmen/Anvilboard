using System.Net;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Translates an HTTP response into the <see cref="ProviderSyncException"/> family.
/// </summary>
/// <remarks>
/// Shared here rather than duplicated per adapter because the mapping is a property of HTTP, not
/// of any one provider: every plugin that talks to a REST or GraphQL endpoint wants the same
/// 401/403/429 reading, and a second copy is a second place for the rate-limit heuristic to drift.
/// </remarks>
public static class ProviderHttpResponse
{
    /// <summary>
    /// Throws a categorized <see cref="ProviderSyncException"/> when
    /// <paramref name="response"/> is unsuccessful, letting the host's sync coordinator choose the
    /// right backoff without reading HTTP itself.
    /// </summary>
    /// <param name="response">The response to inspect. Never read as content.</param>
    /// <param name="providerName">Human-readable provider name used in the exception message.</param>
    public static void EnsureSuccessOrThrowProviderException(
        this HttpResponseMessage response, string providerName)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var retryAfter = ReadRetryAfter(response);

        // A 403 is ambiguous: GitHub returns it for both a revoked token and a secondary rate
        // limit. The presence of a retry hint is what disambiguates them, so quarantining a merely
        // throttled credential (or hammering a revoked one) is avoided.
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && retryAfter is not null))
        {
            throw new ProviderThrottledException(
                retryAfter ?? TimeSpan.FromMinutes(1),
                $"{providerName} rate-limited the request ({(int)response.StatusCode}).");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthenticationException(
                $"{providerName} rejected the configured credentials ({(int)response.StatusCode}).");
        }

        throw new ProviderSyncException(
            $"{providerName} returned {(int)response.StatusCode} {response.StatusCode}.");
    }

    /// <summary>
    /// Reads a retry hint from <c>Retry-After</c> (delta-seconds or HTTP date) or from
    /// <c>X-RateLimit-Reset</c> (Unix seconds), returning null when neither is present or usable.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (header.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero)
                {
                    return until;
                }
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unixSeconds))
        {
            var until = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until;
            }
        }

        return null;
    }
}
