namespace Anvilboard.Domain;

/// <summary>
/// The single top-level tenant boundary for a local Anvilboard instance. Most self-hosted,
/// single-user/single-team installs will have exactly one row here; it exists mainly so the
/// schema doesn't need a breaking change if multiple workspaces are ever hosted from one process.
/// </summary>
public sealed class Workspace
{
    public WorkspaceId Id { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
