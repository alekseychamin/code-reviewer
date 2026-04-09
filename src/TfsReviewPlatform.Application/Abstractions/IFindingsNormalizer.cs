using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IFindingsNormalizer
{
    IReadOnlyList<ReviewFinding> Normalize(IEnumerable<string> rawResponses);

    ChunkReviewNormalizationResult NormalizeChunkReview(IEnumerable<string> rawResponses);
}
