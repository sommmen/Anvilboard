namespace Anvilboard.Domain;

/// <summary>
/// A directional, typed association between two issues. Link types are intentionally free-form:
/// their meaning is rendered by clients and never drives workflow behavior.
/// </summary>
public sealed class IssueLink
{
    public IssueLinkId Id { get; init; }
    public required IssueId SourceIssueId { get; init; }
    public required IssueId TargetIssueId { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public MemberId? CreatedById { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
