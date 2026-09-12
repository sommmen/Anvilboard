using Anvilboard.Domain;

namespace Anvilboard.Application.Workflows;

/// <summary>
/// Adapter-facing projection of a <see cref="WorkflowState"/>. Returned instead of the entity so no
/// adapter is handed a tracked EF object it could mutate outside the service's validation.
/// </summary>
/// <remarks>
/// Carries <em>both</em> spellings of the identifier: <see cref="Key"/> is the lower-snake value the
/// API accepts back, and <see cref="SymbolicKey"/> is the UPPER_SNAKE_CASE form
/// <c>docs/anvilboard/tech-design.md</c> §7.2 specifies for external serialization. Emitting both
/// means a client never has to invert the transform to echo an identifier back.
/// </remarks>
public sealed record WorkflowStateDto(
    Guid Id,
    string Key,
    string SymbolicKey,
    string DisplayName,
    int Order,
    bool IsTerminal,
    bool IsArchived)
{
    public static WorkflowStateDto FromState(WorkflowState state) => new(
        state.Id.Value,
        state.Key,
        state.Key.ToUpperInvariant(),
        state.DisplayName,
        state.Order,
        state.IsTerminal,
        state.IsArchived);
}

/// <summary>
/// Adapter-facing projection of a <see cref="WorkflowTransition"/> edge, denormalizing both endpoint
/// keys so a caller can render the adjacency list without a second round trip for state names.
/// </summary>
public sealed record WorkflowTransitionDto(
    Guid Id,
    Guid FromStateId,
    string FromStateKey,
    Guid ToStateId,
    string ToStateKey);
