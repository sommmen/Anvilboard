namespace Anvilboard.Domain;

/// <summary>A body of work (a "project" in the Linear/GH-Projects sense) owned by a team.</summary>
public sealed class Project
{
    public ProjectId Id { get; init; }
    public required TeamId TeamId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? TargetDate { get; set; }
}
