using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed class ExternalReviewInsights
{
    public static ExternalReviewInsights Empty { get; } = new();

    public ChangeSummaryResult? ChangeSummary { get; init; }

    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

    public IReadOnlyList<ReviewOpportunityItem> Opportunities { get; init; } = [];
}
