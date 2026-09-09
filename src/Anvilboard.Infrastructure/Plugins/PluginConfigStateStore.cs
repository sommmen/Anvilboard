using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Infrastructure.Plugins;

public sealed class PluginConfigStateStore(AnvilboardDbContext dbContext) : IPluginConfigStore, IPluginStateStore
{
    public async Task SetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        string value,
        bool isSecret = false,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(pluginKey, 100, nameof(pluginKey));
        ValidateKey(configKey, 200, nameof(configKey));

        var config = await dbContext.PluginConfigs.FindAsync(
            [workspaceId, pluginKey, configKey], cancellationToken);

        if (config is null)
        {
            dbContext.PluginConfigs.Add(new PluginConfig
            {
                WorkspaceId = workspaceId,
                PluginKey = pluginKey,
                ConfigKey = configKey,
                Value = value,
                IsSecret = isSecret,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            config.Value = value;
            config.IsSecret = isSecret;
            config.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PluginConfigValue?> GetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        CancellationToken cancellationToken = default)
    {
        var config = await dbContext.PluginConfigs.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.PluginKey == pluginKey && candidate.ConfigKey == configKey,
            cancellationToken);

        return config is null
            ? null
            : new PluginConfigValue(config.ConfigKey, config.IsSecret ? null : config.Value, config.IsSecret, config.UpdatedAt);
    }

    public async Task RemoveAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string configKey,
        CancellationToken cancellationToken = default)
    {
        var config = await dbContext.PluginConfigs.FindAsync(
            [workspaceId, pluginKey, configKey], cancellationToken);

        if (config is null)
        {
            return;
        }

        dbContext.PluginConfigs.Remove(config);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    Task IPluginStateStore.SetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        string jsonValue,
        CancellationToken cancellationToken) => SetStateAsync(workspaceId, pluginKey, stateKey, jsonValue, cancellationToken);

    async Task<PluginStateValue?> IPluginStateStore.GetAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.PluginStates.AsNoTracking().SingleOrDefaultAsync(
            state => state.WorkspaceId == workspaceId && state.PluginKey == pluginKey && state.StateKey == stateKey,
            cancellationToken);

        return state is null ? null : new PluginStateValue(state.StateKey, state.Value, state.UpdatedAt);
    }

    Task IPluginStateStore.RemoveAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        CancellationToken cancellationToken) => RemoveStateAsync(workspaceId, pluginKey, stateKey, cancellationToken);

    private async Task SetStateAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        string jsonValue,
        CancellationToken cancellationToken)
    {
        ValidateKey(pluginKey, 100, nameof(pluginKey));
        ValidateKey(stateKey, 200, nameof(stateKey));

        var state = await dbContext.PluginStates.FindAsync([workspaceId, pluginKey, stateKey], cancellationToken);

        if (state is null)
        {
            dbContext.PluginStates.Add(new PluginState
            {
                WorkspaceId = workspaceId,
                PluginKey = pluginKey,
                StateKey = stateKey,
                Value = jsonValue,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            state.Value = jsonValue;
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateKey(string key, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, maxLength, parameterName);
    }

    private async Task RemoveStateAsync(
        WorkspaceId workspaceId,
        string pluginKey,
        string stateKey,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.PluginStates.FindAsync([workspaceId, pluginKey, stateKey], cancellationToken);

        if (state is null)
        {
            return;
        }

        dbContext.PluginStates.Remove(state);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
