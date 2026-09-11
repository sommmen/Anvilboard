using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Infrastructure.Plugins;

/// <summary>
/// Wraps the host's <see cref="IServiceProvider"/> for constructing reflection-loaded plugins,
/// hiding any service whose type implements <see cref="IHostOnlyPluginCapability"/>.
/// </summary>
/// <remarks>
/// <see cref="ActivatorUtilities.CreateInstance"/> resolves a plugin constructor's parameters by
/// asking the provider for each parameter type; a null result is treated exactly like an
/// unregistered service (the constructor is skipped in favor of another, or construction fails and
/// is logged, the same as any other unsatisfiable dependency). Filtering here means a host-only
/// capability like the trusted, workspace-authenticated plugin-event publisher is unreachable by a
/// plugin's constructor even though it lives in the very same container the host itself resolves
/// it from — without <c>Anvilboard.Infrastructure</c> ever needing to reference the concrete type.
/// </remarks>
internal sealed class HostOnlyCapabilityFilteringServiceProvider(IServiceProvider inner) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider) ||
            serviceType == typeof(IServiceScopeFactory) ||
            IsHostOnlyCapability(serviceType))
        {
            return null;
        }

        return inner.GetService(serviceType);
    }

    private static bool IsHostOnlyCapability(Type serviceType)
    {
        if (typeof(IHostOnlyPluginCapability).IsAssignableFrom(serviceType))
        {
            return true;
        }

        return serviceType.IsGenericType &&
            serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
            typeof(IHostOnlyPluginCapability).IsAssignableFrom(serviceType.GetGenericArguments()[0]);
    }
}
