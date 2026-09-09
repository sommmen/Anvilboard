using Anvilboard.Domain;

namespace Anvilboard.Application.Integrations;

/// <summary>Safe integration representation; secret values are never included.</summary>
public sealed record IntegrationDto(
    Guid Id,
    Guid WorkspaceId,
    IntegrationProvider Provider,
    IntegrationStatus Status,
    string SettingsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
