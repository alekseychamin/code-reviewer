namespace TfsReviewPlatform.Domain.Entities;

public sealed class InlineDiscussionOpportunityItem
{
    public string File { get; init; } = string.Empty;

    public string LineHint { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}
