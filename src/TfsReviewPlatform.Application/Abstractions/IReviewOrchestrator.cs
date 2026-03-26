using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Application.Models;

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

    Task<ReviewHistoryDto> GetPullRequestHistoryAsync(string pullRequestUrl, CancellationToken cancellationToken);

    Task DeletePullRequestHistoryAsync(string pullRequestUrl, CancellationToken cancellationToken);

    Task<ReviewRunDto> PublishInlineCommentAsync(Guid runId, Guid commentId, CancellationToken cancellationToken);

    Task<ReviewRunDto> PublishReportAsync(Guid runId, CancellationToken cancellationToken);

    Task<ReviewRunDto> ContinueInlineDiscussionAsync(
        Guid runId,
        Guid commentId,
        ContinueInlineDiscussionRequest request,
        CancellationToken cancellationToken);

    Task<ArtifactDownloadResult?> GetDiffDownloadAsync(Guid runId, CancellationToken cancellationToken);

    Task<ArtifactDownloadResult?> GetMarkdownReportDownloadAsync(Guid runId, CancellationToken cancellationToken);
}
