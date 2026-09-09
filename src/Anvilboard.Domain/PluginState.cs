namespace Anvilboard.Domain;

/// <summary>
/// A durable, opaque runtime-state entry owned by a plugin within a workspace.
/// </summary>
public sealed class PluginState
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string PluginKey { get; init; }
    public required string StateKey { get; init; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
