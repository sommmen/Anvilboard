namespace Anvilboard.Domain;

/// <summary>
/// A workspace and plugin namespaced configuration entry. Secret values are persisted for later
/// routing through the host secret provider and are never returned unredacted by the store.
/// </summary>
public sealed class PluginConfig
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string PluginKey { get; init; }
    public required string ConfigKey { get; init; }
    public required string Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
