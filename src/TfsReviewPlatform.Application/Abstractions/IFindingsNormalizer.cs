using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IFindingsNormalizer
{
    IReadOnlyList<ReviewFinding> Normalize(IEnumerable<string> rawResponses);
}
