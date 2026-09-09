using Anvilboard.Domain;

namespace Anvilboard.Plugins.Abstractions;

/// <summary>
/// Performs a bounded authentication/connectivity check for an integration. Implementations must
/// not import provider data as part of validation.
/// </summary>
public interface IIntegrationValidator
{
    IntegrationProvider Provider { get; }

    Task ValidateAsync(
        IReadOnlyDictionary<string, string> credentials,
        string settingsJson,
        CancellationToken ct = default);
}
