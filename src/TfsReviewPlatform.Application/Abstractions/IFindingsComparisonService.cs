using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IFindingsComparisonService
{
    FindingsComparisonSnapshot Compare(
        Guid? previousRunId,
        IReadOnlyList<ReviewFinding> previousFindings,
        IReadOnlyList<ReviewFinding> currentFindings);
}
