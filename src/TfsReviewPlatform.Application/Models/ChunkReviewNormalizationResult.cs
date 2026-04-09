using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed record ChunkReviewNormalizationResult(
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewOpportunityItem> Opportunities);
