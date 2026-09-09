using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Stores plugin-owned, JSON runtime state in a workspace and plugin namespace.
/// </summary>
public interface IPluginStateStore
{
    Task SetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        string jsonValue,
        CancellationToken cancellationToken = default);

    Task<PluginStateValue?> GetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Opaque JSON state returned to the owning plugin.</summary>
public sealed record PluginStateValue(string StateKey, string Value, DateTimeOffset UpdatedAt);
