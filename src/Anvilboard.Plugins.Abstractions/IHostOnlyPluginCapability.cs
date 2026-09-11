namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Marks a service as host-internal: never resolvable while constructing a reflection-loaded
/// plugin, even though it lives in the same DI container the host itself uses.
/// </summary>
/// <remarks>
/// <see cref="Anvilboard.Infrastructure.Plugins.PluginRegistry"/> constructs out-of-repo plugins
/// with <c>ActivatorUtilities.CreateInstance</c> against the host's own <see cref="IServiceProvider"/>,
/// because that is the only way a plugin's constructor can request the ordinary abstractions it is
/// meant to use (<see cref="IPluginConfigStore"/>, <see cref="IPluginStateStore"/>,
/// <see cref="IPluginEventPublisher"/>, logging, options, and so on). Without this marker, any
/// service registered in that same container — including host-only capabilities that authenticate
/// or route on a caller's behalf — is just as reachable: a plugin whose assembly also references
/// <c>Anvilboard.Application</c> could declare a constructor parameter for one and receive it, no
/// different from a legitimate abstraction. Implementing this interface on such a capability (e.g.
/// a trusted event publisher that lets a caller specify an arbitrary workspace) is what lets
/// <see cref="Anvilboard.Infrastructure.Plugins.PluginRegistry"/> filter it out purely by type,
/// without <c>Anvilboard.Infrastructure</c> ever having to reference the concrete host-only type.
/// This is not part of the plugin-authoring contract: plugins should never implement it themselves.
/// </remarks>
public interface IHostOnlyPluginCapability
{
}
