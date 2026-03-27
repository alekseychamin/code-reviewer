using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Integrations.PullRequests;

internal sealed class PullRequestDiffService(IEnumerable<IPullRequestDiffProvider> providers) : IPullRequestDiffService
{
    private readonly IPullRequestDiffProvider[] _providers = providers.ToArray();

    public Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(item => item.CanHandle(pullRequestUrl));
        if (provider is null)
        {
            throw new InvalidOperationException(
                $"Pull request URL '{pullRequestUrl}' is not supported yet. Supported platforms: Azure DevOps/TFS, GitHub, GitLab.");
        }

        return provider.GetDiffAsync(pullRequestUrl, accessToken, cancellationToken);
    }
}
