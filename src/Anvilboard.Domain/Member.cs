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
}
