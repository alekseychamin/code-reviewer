using TfsReviewPlatform.Application.Contracts.Reviews;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewRequestValidator
{
    IReadOnlyDictionary<string, string[]> Validate(StartPullRequestReviewRequest request);

    IReadOnlyDictionary<string, string[]> Validate(StartBranchReviewRequest request);
}
