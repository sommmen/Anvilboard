namespace Anvilboard.Domain;

/// <summary>
/// A workspace-scoped credential issued to automation (an ingestion bot, a coding agent, a CI
/// job) or a browser session (the web SPA). Only a deterministic, unsalted hash of the raw token
/// value is ever persisted — high-entropy random values make an unsalted hash safe and allow
/// direct lookup by hash — the raw value is shown to the caller exactly once, at issuance, and is
/// never recoverable afterward (NFR-SEC-001).
/// </summary>
public sealed class ApiToken
{
    public ApiTokenId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>The <see cref="Member"/> (normally a <see cref="Role.AutomationAgent"/>) this
    /// token authenticates as.</summary>
    public required MemberId MemberId { get; init; }

    /// <summary>Deterministic, unsalted hash of the raw token value. Never the raw value itself.</summary>
    public required string TokenHash { get; init; }

    /// <summary>The permission subset granted to this token, intersected with the member's role
    /// default at authorization time. Fixed at issuance.</summary>
    public required IReadOnlyList<Permission> GrantedPermissions { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Set the instant the token is revoked; revocation takes effect immediately and is
    /// never deferred.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
