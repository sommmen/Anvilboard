namespace Anvilboard.Domain;

public sealed class Comment
{
    public CommentId Id { get; init; }
    public required IssueId IssueId { get; set; }
    public MemberId? AuthorId { get; set; }
    public required string Body { get; set; }
    public IntegrationProvider Source { get; set; } = IntegrationProvider.Local;
    public DateTimeOffset CreatedAt { get; init; }
}
