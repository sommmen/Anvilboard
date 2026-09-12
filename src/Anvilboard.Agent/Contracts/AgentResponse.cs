namespace Anvilboard.Agent.Contracts;

/// <summary>Contract-level constants for the CLI/MCP surface.</summary>
public static class AgentContract
{
    /// <summary>
    /// The agent surface contract version (NFR-MNT-001, AC-105), emitted on every response.
    /// Bumped only on a breaking change to operation inputs or to the response envelope.
    /// </summary>
    public const string ApiVersion = "1.0";
}

/// <summary>
/// The envelope every agent operation returns: a version a caller can branch on, the correlation
/// id that ties the invocation to its audit and log records, and the operation's own payload.
/// </summary>
/// <remarks>
/// Returning the envelope from the operation method (rather than wrapping it in the host) keeps it
/// visible in the MCP-generated output schema and lets a compile-time/catalog test assert that
/// every operation carries a version — AC-105 holds by construction rather than by convention.
/// </remarks>
public sealed record AgentResponse<T>(string ApiVersion, string CorrelationId, T Data)
{
    public static AgentResponse<T> For(string correlationId, T data) =>
        new(AgentContract.ApiVersion, correlationId, data);
}
