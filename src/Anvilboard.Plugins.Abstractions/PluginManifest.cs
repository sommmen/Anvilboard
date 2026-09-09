namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Static identity/metadata every plugin exposes so the host can list, health-check, and
/// attribute synced data to it without loading provider-specific types. Implemented as a simple
/// property on <see cref="IAnvilboardPlugin"/> rather than an attribute so it can be computed
/// (e.g. version read from the assembly) instead of only declared.
/// </summary>
/// <param name="Key">
/// Stable, unique, lowercase identifier for this plugin (e.g. "github", "linear",
/// "slack-tickets"). Used as the namespace prefix for <c>ExternalLink.SourceKey</c> values and in
/// configuration sections (<c>Plugins:&lt;Key&gt;</c>).
/// </param>
/// <param name="DisplayName">Human-readable name shown in the UI's integrations page.</param>
/// <param name="Version">Informational version string, shown in diagnostics/CLI output.</param>
/// <param name="SupportedContractVersion">Plugin API contract version required by this plugin.</param>
public sealed record PluginManifest(
    string Key,
    string DisplayName,
    string Version,
    string SupportedContractVersion = PluginContract.Version)
{
    public PluginManifest(string key, string displayName, string version)
        : this(key, displayName, version, PluginContract.Version)
    {
    }
}

/// <summary>Version of the plugin contract supported by this host release.</summary>
public static class PluginContract
{
    public const string Version = "1.0";
}

/// <summary>
/// Marker/root interface every plugin type (ingestion source, webhook receiver, or issue hook)
/// implements so the host's plugin registry can discover, describe, and enable/disable it
/// uniformly regardless of which of the more specific interfaces it also implements.
/// </summary>
public interface IAnvilboardPlugin
{
    PluginManifest Manifest { get; }
}
