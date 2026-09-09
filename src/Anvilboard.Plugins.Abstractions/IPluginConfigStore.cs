using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Stores administrator-managed configuration in a workspace and plugin namespace. Secret values
/// are write-only: reads expose their presence but never the persisted value.
/// </summary>
public interface IPluginConfigStore
{
    Task SetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        string value,
        bool isSecret = false,
        CancellationToken cancellationToken = default);

    Task<PluginConfigValue?> GetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A configuration value safe to return to callers. <see cref="Value"/> is <see langword="null"/>
/// whenever <see cref="IsSecret"/> is true.
/// </summary>
public sealed record PluginConfigValue(string ConfigKey, string? Value, bool IsSecret, DateTimeOffset UpdatedAt);
