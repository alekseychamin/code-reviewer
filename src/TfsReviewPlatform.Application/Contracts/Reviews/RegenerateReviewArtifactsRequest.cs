namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class RegenerateReviewArtifactsRequest
{
    public bool RegenerateDescription { get; init; }

    public bool RegenerateDiagram { get; init; }
}
