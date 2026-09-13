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

    /// <summary>Settable because a mistyped link is corrected in place rather than re-created,
    /// which would otherwise discard <see cref="CreatedAt"/> and the link's activity history.</summary>
    public required string Type { get; set; }

    public required string Description { get; set; }
    public MemberId? CreatedById { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// The advisory link-type vocabulary. Served to clients so the suggestion list has one source
/// instead of being hard-coded per surface; it is <em>not</em> enforced, because
/// <see cref="IssueLink.Type"/> is deliberately free-form.
/// </summary>
public static class IssueLinkTypes
{
    public const string Related = "RELATED";
    public const string Blocks = "BLOCKS";
    public const string BlockedBy = "BLOCKED_BY";
    public const string Duplicates = "DUPLICATES";
    public const string Parent = "PARENT";
    public const string Child = "CHILD";

    /// <summary>Suggested types, in the order clients should present them.</summary>
    public static readonly IReadOnlyList<string> Suggested =
    [
        Related,
        Blocks,
        BlockedBy,
        Duplicates,
        Parent,
        Child,
    ];
}
