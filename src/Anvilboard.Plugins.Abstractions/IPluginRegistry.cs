namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Read-side catalog of every plugin currently loaded into the host process, regardless of which
/// of <see cref="IIngestionSource"/>, <see cref="IWebhookReceiver"/>, or <see cref="IIssueHook"/>
/// it implements (a single plugin type may implement more than one). The concrete implementation
/// lives in <c>Anvilboard.Infrastructure</c> and populates itself by scanning assemblies listed in
/// configuration (<c>Plugins:Assemblies</c>) plus whatever is registered in DI at startup by the
/// two in-repo integrations, so first-class and third-party plugins are discovered identically.
/// </summary>
public interface IPluginRegistry
{
    IReadOnlyList<IAnvilboardPlugin> All { get; }
    IReadOnlyList<IIngestionSource> IngestionSources { get; }
    IReadOnlyList<IWebhookReceiver> WebhookReceivers { get; }
    IReadOnlyList<IIssueHook> IssueHooks { get; }
}
