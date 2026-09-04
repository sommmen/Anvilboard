namespace Anvilboard.Infrastructure.Plugins;

/// <summary>Bound from the "Plugins" configuration section.</summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// Paths to additional plugin assembly DLLs to load at startup, beyond whatever is already
    /// referenced by the host project. This is how a private, out-of-repo plugin (e.g. a Slack
    /// ticket-creation library) is enabled without the host ever referencing its source: build it
    /// separately against <c>Anvilboard.Plugins.Abstractions</c>, drop the DLL somewhere on disk,
    /// and list its path here.
    /// </summary>
    public List<string> AssemblyPaths { get; set; } = [];
}
