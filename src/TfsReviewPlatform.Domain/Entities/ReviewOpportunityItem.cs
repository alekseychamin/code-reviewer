namespace TfsReviewPlatform.Domain.Entities;

public sealed record ReviewOpportunityItem(
    string File,
    string LineHint,
    string Title,
    string Description,
    string Suggestion,
    int StartLine = 0);
