using Anvilboard.Domain;

namespace Anvilboard.Application.Workflows;

/// <summary>
/// Explicit actor, audit channel, and correlation id for a workflow-configuration mutation
/// (<c>docs/plans/workflow-admin-surface.md</c> §8.1). Callers (REST, CLI, MCP) supply this rather
/// than letting <see cref="WorkflowEngine"/> infer the channel from runtime type or ambient process
/// mode, which is what makes <c>FR-WS-002 AC4</c> — "every configuration mutation emits an audit
/// event" — structural rather than a convention each adapter must remember (DR-WFA-001).
/// </summary>
public sealed record WorkflowOperationContext(string ActorId, AuditChannel Channel, string CorrelationId);
