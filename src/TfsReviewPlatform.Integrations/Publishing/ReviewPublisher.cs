using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.Publishing;

internal sealed class ReviewPublisher(IEnumerable<IPullRequestReviewPublisherProvider> providers) : IReviewPublisher
{
    private readonly IPullRequestReviewPublisherProvider[] _providers = providers.ToArray();

    public Task<bool> PublishReportAsync(
        string pullRequestUrl,
        string accessToken,
        string reportContent,
        CancellationToken cancellationToken)
    {
        return ResolveProvider(pullRequestUrl).PublishReportAsync(
            pullRequestUrl,
            accessToken,
            reportContent,
            cancellationToken);
    }

    public Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken)
    {
        return ResolveProvider(pullRequestUrl).PublishAsync(
            pullRequestUrl,
            accessToken,
            publishMode,
            summaryComment,
            inlineComments,
            cancellationToken);
    }

    public Task<bool> PublishInlineCommentAsync(
        string pullRequestUrl,
        string accessToken,
        InlineCommentDraft inlineComment,
        CancellationToken cancellationToken)
    {
        return ResolveProvider(pullRequestUrl).PublishInlineCommentAsync(
            pullRequestUrl,
            accessToken,
            inlineComment,
            cancellationToken);
    }

    private IPullRequestReviewPublisherProvider ResolveProvider(string pullRequestUrl)
    {
        var provider = _providers.FirstOrDefault(item => item.CanHandle(pullRequestUrl));
        if (provider is null)
        {
            throw new InvalidOperationException(
                $"Pull request URL '{pullRequestUrl}' is not supported for publishing yet. Supported platforms: Azure DevOps/TFS, GitHub.");
        }

        return provider;
    }
}
