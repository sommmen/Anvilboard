namespace Anvilboard.Domain;

public sealed class Label
{
    public LabelId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>Hex color (e.g. "#6E56CF") used for the label chip in the UI.</summary>
    public required string Color { get; set; }
}
