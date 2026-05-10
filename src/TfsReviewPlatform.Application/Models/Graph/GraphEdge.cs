namespace TfsReviewPlatform.Application.Models.Graph;

public sealed class GraphEdge
{
    public required string From { get; init; }

    public required string To { get; init; }

    public required string Kind { get; init; }
}
