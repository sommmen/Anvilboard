using System.Text.Json;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Anvilboard.Plugins.Abstractions.Security;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Integrations;

/// <summary>Owns the workspace-scoped integration lifecycle and never returns credential material.</summary>
public sealed class IntegrationService(
    AnvilboardDbContext db,
    ISecretStore secretStore,
    IWorkspaceAuthorizationService authorizationService,
    IEnumerable<IIntegrationValidator> validators) : IIntegrationService
{
    public async Task<IntegrationDto> ConfigureAsync(
        ActorContext actor,
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> credentials,
        string settingsJson,
        CancellationToken ct = default)
    {
        await EnsureAuthorizedAsync(actor, actor.WorkspaceId, ct);
        ValidateConfiguration(provider, credentials, settingsJson);

        var now = DateTimeOffset.UtcNow;
        var integration = new Integration
        {
            Id = IntegrationId.New(),
            WorkspaceId = actor.WorkspaceId,
            Provider = provider,
            ProtectedCredentials = secretStore.Protect(credentials),
            SettingsJson = settingsJson,
            Status = IntegrationStatus.Configured,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Integrations.Add(integration);
        await db.SaveChangesAsync(ct);

        return ToDto(integration);
    }

    public async Task<IntegrationDto> ValidateAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default)
    {
        var integration = await GetAuthorizedIntegrationAsync(actor, integrationId, ct);
        if (integration.ProtectedCredentials is null)
        {
            throw new IntegrationException("PROVIDER_UNAVAILABLE", "The integration credentials are unavailable.");
        }

        IReadOnlyDictionary<string, string> credentials;
        try
        {
            credentials = secretStore.Unprotect(integration.ProtectedCredentials);
        }
        catch (Exception)
        {
            throw new IntegrationException("PROVIDER_UNAVAILABLE", "The integration credentials are unavailable.");
        }

        var validator = validators.SingleOrDefault(candidate => candidate.Provider == integration.Provider);
        if (validator is null)
        {
            throw new IntegrationException("PROVIDER_UNAVAILABLE", "The integration provider cannot be validated.");
        }

        try
        {
            await validator.ValidateAsync(credentials, integration.SettingsJson, ct);
        }
        catch (IntegrationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IntegrationException("PROVIDER_UNAVAILABLE", "The integration provider could not be reached.");
        }

        return ToDto(integration);
    }

    public async Task<IntegrationDto> EnableAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default)
    {
        var integration = await GetAuthorizedIntegrationAsync(actor, integrationId, ct);
        if (integration.Status == IntegrationStatus.Removed)
        {
            throw new IntegrationException("REFERENCED_ENTITY_NOT_FOUND", "The integration has been removed.");
        }

        integration.Status = IntegrationStatus.Enabled;
        integration.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(integration);
    }

    public async Task<IntegrationDto> PauseAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct = default)
    {
        var integration = await GetAuthorizedIntegrationAsync(actor, integrationId, ct);
        if (integration.Status == IntegrationStatus.Removed)
        {
            throw new IntegrationException("REFERENCED_ENTITY_NOT_FOUND", "The integration has been removed.");
        }

        integration.Status = IntegrationStatus.Paused;
        integration.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(integration);
    }

    public async Task RemoveAsync(ActorContext actor, IntegrationId integrationId, bool confirm, CancellationToken ct = default)
    {
        if (!confirm)
        {
            throw new IntegrationException("VALIDATION_FAILED", "Integration removal requires explicit confirmation.");
        }

        var integration = await GetAuthorizedIntegrationAsync(actor, integrationId, ct);
        if (integration.Status == IntegrationStatus.Removed)
        {
            return;
        }

        integration.ProtectedCredentials = null;
        integration.Status = IntegrationStatus.Removed;
        integration.RemovedAt = DateTimeOffset.UtcNow;
        integration.UpdatedAt = integration.RemovedAt.Value;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Integration> GetAuthorizedIntegrationAsync(ActorContext actor, IntegrationId integrationId, CancellationToken ct)
    {
        var integration = await db.Integrations.SingleOrDefaultAsync(candidate => candidate.Id == integrationId, ct)
            ?? throw new IntegrationException("REFERENCED_ENTITY_NOT_FOUND", "The integration was not found.");
        await EnsureAuthorizedAsync(actor, integration.WorkspaceId, ct);
        return integration;
    }

    private async Task EnsureAuthorizedAsync(ActorContext actor, WorkspaceId workspaceId, CancellationToken ct)
    {
        var result = await authorizationService.AuthorizeAsync(actor, workspaceId, Permission.ManageIntegrations, ct);
        if (!result.IsAuthorized)
        {
            throw new IntegrationException(result.ErrorCode ?? "WORKSPACE_ACCESS_DENIED", "The actor is not authorized to manage integrations in this workspace.");
        }
    }

    private static void ValidateConfiguration(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> credentials,
        string settingsJson)
    {
        if (provider is IntegrationProvider.Local || !Enum.IsDefined(provider))
        {
            throw new IntegrationException("VALIDATION_FAILED", "An external integration provider must be selected.");
        }

        if (credentials.Count == 0 || credentials.Any(credential => string.IsNullOrWhiteSpace(credential.Key) || string.IsNullOrWhiteSpace(credential.Value)))
        {
            throw new IntegrationException("VALIDATION_FAILED", "At least one non-empty credential is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new IntegrationException("VALIDATION_FAILED", "Integration settings must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            throw new IntegrationException("VALIDATION_FAILED", "Integration settings must be valid JSON.");
        }
    }

    private static IntegrationDto ToDto(Integration integration) => new(
        integration.Id.Value,
        integration.WorkspaceId.Value,
        integration.Provider,
        integration.Status,
        integration.SettingsJson,
        integration.CreatedAt,
        integration.UpdatedAt);
}
