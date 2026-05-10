namespace TfsReviewPlatform.Application.Models.Graph;

public sealed class GraphNode
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public string? AssemblyName { get; init; }
}
