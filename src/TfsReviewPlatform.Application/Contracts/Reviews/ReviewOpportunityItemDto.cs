namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewOpportunityItemDto
{
    public string File { get; init; } = string.Empty;

    public string LineHint { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;
}
