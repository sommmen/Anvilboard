namespace Anvilboard.Domain;

/// <summary>A grouping of people/projects that shares one issue key prefix (e.g. "ENG-123").</summary>
public sealed class Team
{
    public TeamId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>Short uppercase prefix used to build human-readable issue keys, e.g. "ENG".</summary>
    public required string Key { get; set; }

    /// <summary>Monotonically increasing counter used to mint the numeric part of issue keys.</summary>
    public int NextIssueNumber { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; init; }
}
