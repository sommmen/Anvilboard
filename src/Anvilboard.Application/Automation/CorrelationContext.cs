namespace Anvilboard.Application.Automation;

/// <summary>
/// A per-call correlation id, resolved identically by every channel (REST, CLI, MCP) so the same
/// value appears in the response envelope, logs, and the corresponding audit record. Deliberately
/// free of any transport dependency: REST resolves it from the <c>X-Correlation-Id</c> header;
/// CLI/MCP generate one per invocation/tool call with no header equivalent
/// (see docs/features/agent-and-automation-surface.md, "Correlation ID Propagation").
/// </summary>
public sealed class CorrelationContext
{
    public string CorrelationId { get; }

    private CorrelationContext(string correlationId)
    {
        CorrelationId = correlationId;
    }

    /// <summary>Uses <paramref name="clientSupplied"/> when it is non-blank, otherwise generates a
    /// new random correlation id.</summary>
    public static CorrelationContext FromHeaderOrNew(string? clientSupplied) =>
        new(string.IsNullOrWhiteSpace(clientSupplied) ? Guid.NewGuid().ToString() : clientSupplied);
}
