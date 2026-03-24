using TfsReviewPlatform.Application.Contracts.Reviews;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewOrchestrator
{
    Task<ReviewRunDto> StartPullRequestReviewAsync(
        StartPullRequestReviewRequest request,
        CancellationToken cancellationToken);

    Task<ReviewRunDto> StartBranchReviewAsync(
        StartBranchReviewRequest request,
        CancellationToken cancellationToken);

    Task<ReviewRunDto?> GetAsync(Guid runId, CancellationToken cancellationToken);
}
