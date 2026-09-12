using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anvilboard.Agent.Automation;

/// <summary>
/// Raised when an operation input fails a host-level precondition that has a stable error code in
/// <c>ErrorCodeCatalog</c>.
/// </summary>
/// <remarks>
/// <c>OperationInvoker</c> flattens every thrown exception to its message, so the message carries
/// the error code verbatim and callers can match on it regardless of channel.
/// </remarks>
public sealed class AgentRequestException(string errorCode, string detail)
    : InvalidOperationException($"{errorCode}: {detail}")
{
    public string ErrorCode { get; } = errorCode;
    public string Detail { get; } = detail;
}

/// <summary>
/// Input preconditions enforced identically on every mutating agent operation.
/// </summary>
public static class AgentRequestGuard
{
    /// <summary>
    /// Maximum accepted idempotency key length, matching the column width used by the
    /// idempotency store.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// Validates a caller-supplied idempotency key (AC-101: absent or malformed keys are rejected
    /// before any write occurs).
    /// </summary>
    /// <remarks>
    /// The key is required rather than optional. An optional key would silently degrade to
    /// at-least-once semantics exactly when it matters — an agent retrying after a timeout is the
    /// case duplicate suppression exists for, and that agent has no way to know suppression was
    /// skipped.
    /// </remarks>
    public static string RequireIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new AgentRequestException(
                "VALIDATION_FAILED",
                "An idempotencyKey is required for mutating operations so retries do not duplicate work.");
        }

        var trimmed = idempotencyKey.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new AgentRequestException(
                "VALIDATION_FAILED",
                $"idempotencyKey must be at most {MaxIdempotencyKeyLength} characters.");
        }

        return trimmed;
    }
}

/// <summary>
/// Produces the request fingerprint stored alongside an idempotency key, so replaying a key with
/// different inputs is detectable rather than silently returning an unrelated stored result.
/// </summary>
public static class CanonicalRequestHash
{
    private static readonly JsonSerializerOptions HashOptions = new()
    {
        // Deliberately independent of the host's display serializer options: the hash must stay
        // stable across formatting or naming-policy changes, otherwise upgrading the host would
        // invalidate every stored idempotency record.
        WriteIndented = false,
    };

    /// <summary>
    /// Hashes the operation name together with its semantically significant inputs. The operation
    /// name is included so the same key used against two different operations is a reuse conflict
    /// rather than a replay.
    /// </summary>
    public static string Compute(string operationName, params object?[] inputs)
    {
        var payload = new StringBuilder(operationName);
        foreach (var input in inputs ?? [])
        {
            payload.Append('\u001f');
            payload.Append(input is null ? "\0null" : JsonSerializer.Serialize(input, HashOptions));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return Convert.ToHexStringLower(bytes);
    }
}
