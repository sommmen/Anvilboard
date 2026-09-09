namespace Anvilboard.Domain;

/// <summary>The lifecycle state of a configured external integration.</summary>
public enum IntegrationStatus
{
    Configured = 0,
    Enabled = 1,
    Paused = 2,
    Removed = 3,
}

/// <summary>
/// A workspace-scoped connection to an approved external provider. Credential data is retained
/// only as a Data Protection payload and is never exposed from this aggregate.
/// </summary>
public sealed class Integration
{
    public IntegrationId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public IntegrationProvider Provider { get; init; }
    public string? ProtectedCredentials { get; set; }
    public required string SettingsJson { get; set; }
    public IntegrationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}
