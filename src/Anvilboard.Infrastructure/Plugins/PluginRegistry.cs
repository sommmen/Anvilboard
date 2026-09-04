using System.Reflection;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Plugins;

/// <summary>
/// Default <see cref="IPluginRegistry"/>. Combines two discovery paths into one uniform list:
/// (1) plugins already registered in DI as <see cref="IAnvilboardPlugin"/> — this is how the
/// in-repo, first-class GitHub and Linear integrations are wired, via a normal
/// <c>services.AddSingleton&lt;IAnvilboardPlugin, GitHubIngestionSource&gt;()</c> call in their
/// own <c>AddXyzIntegration</c> extension method — and (2) plugins loaded by reflection from the
/// external assembly paths in <see cref="PluginHostOptions.AssemblyPaths"/>, which is how private,
/// out-of-repo plugins (e.g. a Slack ticket-creation library) are enabled purely via
/// configuration, with no reference from this solution.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly List<IAnvilboardPlugin> _all = [];

    public PluginRegistry(
        IEnumerable<IAnvilboardPlugin> registeredPlugins,
        IServiceProvider serviceProvider,
        IOptions<PluginHostOptions> options,
        ILogger<PluginRegistry> logger)
    {
        _all.AddRange(registeredPlugins);

        foreach (var path in options.Value.AssemblyPaths)
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var pluginType in assembly.GetTypes().Where(IsPluginImplementation))
                {
                    var plugin = (IAnvilboardPlugin)ActivatorUtilities.CreateInstance(serviceProvider, pluginType);
                    _all.Add(plugin);
                    logger.LogInformation(
                        "Loaded plugin {PluginKey} ({PluginType}) from {AssemblyPath}",
                        plugin.Manifest.Key, pluginType.FullName, path);
                }
            }
            catch (Exception ex)
            {
                // A single misconfigured or incompatible plugin assembly must not prevent the
                // rest of the host (or other plugins) from starting.
                logger.LogError(ex, "Failed to load plugin assembly {AssemblyPath}", path);
            }
        }
    }

    public IReadOnlyList<IAnvilboardPlugin> All => _all;
    public IReadOnlyList<IIngestionSource> IngestionSources => [.. _all.OfType<IIngestionSource>()];
    public IReadOnlyList<IWebhookReceiver> WebhookReceivers => [.. _all.OfType<IWebhookReceiver>()];
    public IReadOnlyList<IIssueHook> IssueHooks => [.. _all.OfType<IIssueHook>()];

    private static bool IsPluginImplementation(Type type) =>
        type is { IsClass: true, IsAbstract: false } && typeof(IAnvilboardPlugin).IsAssignableFrom(type);
}
