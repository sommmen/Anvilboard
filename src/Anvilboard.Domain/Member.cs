namespace Anvilboard.Domain;

/// <summary>A person (or bot/agent identity) that can be an issue assignee or comment author.</summary>
public sealed class Member
{
    public MemberId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>True for non-human actors such as an ingestion bot or a coding agent.</summary>
    public bool IsAgent { get; set; }

    /// <summary>The member's workspace-scoped role, used by <c>WorkspaceAuthorizationService</c>
    /// to resolve permissions. Defaults to the least-privileged human role.</summary>
    public Role Role { get; set; } = Role.Contributor;

    /// <summary>Local login name for human members. Unique within a workspace; null for
    /// members that only authenticate via <see cref="ApiToken"/> (e.g. ingestion bots).</summary>
    public string? Username { get; set; }

    /// <summary>Salted hash of the member's local password. Never the raw value itself
    /// (NFR-SEC-001). Null for members without a local username/password credential.</summary>
    public string? PasswordHash { get; set; }
}
