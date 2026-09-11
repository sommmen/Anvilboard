using Anvilboard.Infrastructure.Plugins;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Infrastructure.Tests.Plugins;

public sealed class PluginRegistryTests
{
    [Fact]
    public void ThreeArgumentManifestConstructor_DefaultsToCurrentContractVersion()
    {
        var manifest = new PluginManifest("test", "Test", "1.0.0");

        Assert.Equal(PluginContract.Version, manifest.SupportedContractVersion);
        Assert.NotNull(typeof(PluginManifest).GetConstructor(
            [typeof(string), typeof(string), typeof(string)]));
    }

    [Fact]
    public void IncompatibleContractVersion_SkippedWithoutCrashingHost()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions { AssemblyPaths = [typeof(IncompatibleContractPlugin).Assembly.Location] });

        var registry = new PluginRegistry([], services, options, NullLogger<PluginRegistry>.Instance);

        Assert.DoesNotContain(registry.All, plugin => plugin.Manifest.Key == "incompatible-test");
        Assert.Contains(registry.All, plugin => plugin.Manifest.Key == "compatible-test");
    }

    [Fact]
    public void ReflectionLoadedPlugin_CannotResolveHostOnlyCapability()
    {
        // A reflection-loaded plugin whose constructor requests a host-only capability (marked
        // IHostOnlyPluginCapability, e.g. the trusted, workspace-authenticated plugin-event
        // publisher webhooks use) must not be able to receive it, even though that capability is
        // registered in the very same container the host resolves it from.
        var services = new ServiceCollection();
        services.AddSingleton<IHostOnlyCapabilityProbe, HostOnlyCapabilityProbe>();
        using var provider = services.BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions
        {
            AssemblyPaths = [typeof(PluginRequiringHostOnlyCapability).Assembly.Location],
        });

        var registry = new PluginRegistry([], provider, options, NullLogger<PluginRegistry>.Instance);

        Assert.DoesNotContain(registry.All, plugin => plugin.Manifest.Key == "requires-host-only-capability");
    }

    [Fact]
    public void ReflectionLoadedPlugin_CannotResolveRawServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostOnlyCapabilityProbe, HostOnlyCapabilityProbe>();
        using var provider = services.BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions
        {
            AssemblyPaths = [typeof(PluginRequiringServiceProvider).Assembly.Location],
        });

        var registry = new PluginRegistry([], provider, options, NullLogger<PluginRegistry>.Instance);

        Assert.DoesNotContain(registry.All, plugin => plugin.Manifest.Key == "requires-service-provider");
    }

    [Fact]
    public void ReflectionLoadedPlugin_CannotResolveHostOnlyCapabilityCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostOnlyCapabilityProbe, HostOnlyCapabilityProbe>();
        using var provider = services.BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions
        {
            AssemblyPaths = [typeof(PluginRequiringHostOnlyCapabilityCollection).Assembly.Location],
        });

        var registry = new PluginRegistry([], provider, options, NullLogger<PluginRegistry>.Instance);

        Assert.DoesNotContain(registry.All, plugin => plugin.Manifest.Key == "requires-host-only-capability-collection");
    }

    [Fact]
    public void ReflectionLoadedPlugin_CanStillResolveOrdinaryServices()
    {
        // Filtering host-only capabilities out of plugin construction must not affect ordinary
        // constructor-injectable services a plugin is meant to use.
        var services = new ServiceCollection();
        services.AddSingleton<IOrdinaryCapabilityProbe, OrdinaryCapabilityProbe>();
        using var provider = services.BuildServiceProvider();
        var options = Options.Create(new PluginHostOptions
        {
            AssemblyPaths = [typeof(PluginRequiringOrdinaryCapability).Assembly.Location],
        });

        var registry = new PluginRegistry([], provider, options, NullLogger<PluginRegistry>.Instance);

        Assert.Contains(registry.All, plugin => plugin.Manifest.Key == "requires-ordinary-capability");
    }

    public sealed class IncompatibleContractPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("incompatible-test", "Incompatible Test", "1.0.0", "2.0");
    }

    public sealed class CompatibleContractPlugin : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("compatible-test", "Compatible Test", "1.0.0");
    }

    public interface IHostOnlyCapabilityProbe : IHostOnlyPluginCapability;

    public sealed class HostOnlyCapabilityProbe : IHostOnlyCapabilityProbe;

    public sealed class PluginRequiringHostOnlyCapability(IHostOnlyCapabilityProbe probe) : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("requires-host-only-capability", "Requires Host-Only Capability", "1.0.0");
    }

    public sealed class PluginRequiringServiceProvider(IServiceProvider serviceProvider) : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("requires-service-provider", "Requires Service Provider", "1.0.0");
    }

    public sealed class PluginRequiringHostOnlyCapabilityCollection(IEnumerable<IHostOnlyCapabilityProbe> probes) : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("requires-host-only-capability-collection", "Requires Host-Only Capability Collection", "1.0.0");
    }

    public interface IOrdinaryCapabilityProbe;

    public sealed class OrdinaryCapabilityProbe : IOrdinaryCapabilityProbe;

    public sealed class PluginRequiringOrdinaryCapability(IOrdinaryCapabilityProbe probe) : IAnvilboardPlugin
    {
        public PluginManifest Manifest { get; } = new("requires-ordinary-capability", "Requires Ordinary Capability", "1.0.0");
    }
}
