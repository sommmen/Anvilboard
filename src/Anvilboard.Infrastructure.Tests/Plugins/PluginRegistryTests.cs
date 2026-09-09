using Anvilboard.Infrastructure.Plugins;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Tests.Plugins;

public sealed class PluginRegistryTests
{
    [Fact]
    public void IncompatibleContractVersion_SkippedWithoutCrashingHost()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions { AssemblyPaths = [typeof(IncompatibleContractPlugin).Assembly.Location] });

        var registry = new PluginRegistry([], services, options, NullLogger<PluginRegistry>.Instance);

        Assert.DoesNotContain(registry.All, plugin => plugin.Manifest.Key == "incompatible-test");
        Assert.Contains(registry.All, plugin => plugin.Manifest.Key == "compatible-test");
    }

    public sealed class IncompatibleContractPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("incompatible-test", "Incompatible Test", "1.0.0", "2.0");
    }

    public sealed class CompatibleContractPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("compatible-test", "Compatible Test", "1.0.0");
    }
}
