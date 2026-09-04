namespace Anvilboard.Domain;

/// <summary>
/// Identifies which external system produced or owns a piece of synced data. GitHub and Linear
/// are modeled as first-class, built-in values because their integration projects
/// (<c>Anvilboard.Integrations.GitHub</c>/<c>.Linear</c>) ship in this repository. Anything else
/// (Slack, a custom agent push, a private plugin) is represented by <see cref="Custom"/> plus the
/// plugin-supplied <see cref="ExternalLink.SourceKey"/> string, so third-party plugins never need
/// to modify this enum to identify themselves.
/// </summary>
public enum IntegrationProvider
{
    Local = 0,
    GitHub = 1,
    Linear = 2,
    Custom = 99,
}
