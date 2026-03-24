using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IPullRequestDiffService
{
    Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string accessToken,
        CancellationToken cancellationToken);
}
