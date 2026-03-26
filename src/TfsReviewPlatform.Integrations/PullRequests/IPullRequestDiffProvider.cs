using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Integrations.PullRequests;

internal interface IPullRequestDiffProvider
{
    bool CanHandle(string pullRequestUrl);

    Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string? accessToken,
        CancellationToken cancellationToken);
}
