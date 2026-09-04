namespace Anvilboard.Domain;

/// <summary>
/// Strongly-typed identifier base shared by every aggregate id in the domain. Using distinct
/// struct types (rather than raw <see cref="Guid"/>) prevents accidentally passing a
/// <see cref="TeamId"/> where an <see cref="IssueId"/> is expected at compile time.
/// </summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}

public readonly record struct WorkspaceId(Guid Value) : IStronglyTypedId
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct TeamId(Guid Value) : IStronglyTypedId
{
    public static TeamId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProjectId(Guid Value) : IStronglyTypedId
{
    public static ProjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct IssueId(Guid Value) : IStronglyTypedId
{
    public static IssueId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct LabelId(Guid Value) : IStronglyTypedId
{
    public static LabelId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct CommentId(Guid Value) : IStronglyTypedId
{
    public static CommentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ActivityEventId(Guid Value) : IStronglyTypedId
{
    public static ActivityEventId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ExternalLinkId(Guid Value) : IStronglyTypedId
{
    public static ExternalLinkId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct MemberId(Guid Value) : IStronglyTypedId
{
    public static MemberId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
