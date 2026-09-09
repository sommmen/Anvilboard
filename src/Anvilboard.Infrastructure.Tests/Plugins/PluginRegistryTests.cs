using Anvilboard.Infrastructure.Plugins;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Tests.Plugins;

public sealed class PluginRegistryTests
{
    [Fact]
    public void Constructor_SkipsPluginsWithUnsupportedContractVersion()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions
        {
            AssemblyPaths = [typeof(PluginRegistryTests).Assembly.Location],
        });

        var registry = new PluginRegistry(
            [],
            serviceProvider,
            options,
            NullLogger<PluginRegistry>.Instance);

        var loaded = Assert.Single(registry.All);
        Assert.IsType<CompatibleTestPlugin>(loaded);
    }

    public sealed class CompatibleTestPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("compatible", "Compatible", "1.0.0");
    }

    public sealed class IncompatibleTestPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new(
            "incompatible",
            "Incompatible",
            "1.0.0",
            PluginManifest.CurrentContractVersion + 1);
    }
}
