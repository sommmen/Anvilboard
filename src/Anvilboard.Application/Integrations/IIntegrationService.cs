using Anvilboard.Application.Authorization;
using Anvilboard.Domain;

namespace Anvilboard.Application.Integrations;

public interface IIntegrationService
{
    Task<IntegrationDto> ConfigureAsync(
        ActorContext actor,
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> credentials,
        string settingsJson,
        CancellationToken ct = default);

    Task<IntegrationDto> ValidateAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default);
    Task<IntegrationDto> EnableAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default);
    Task<IntegrationDto> PauseAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default);
    Task RemoveAsync(ActorContext actor, IntegrationId integrationId, bool confirm, CancellationToken ct = default);
}
