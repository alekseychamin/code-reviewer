using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewCodeSemanticContextService
{
    Task<IReadOnlyList<ReviewWorkspaceToolResponse>> BuildContextAsync(
        Guid runId,
        DiffAcquisitionResult diffResult,
        PreprocessedDiff preprocessed,
        CancellationToken cancellationToken);
}
