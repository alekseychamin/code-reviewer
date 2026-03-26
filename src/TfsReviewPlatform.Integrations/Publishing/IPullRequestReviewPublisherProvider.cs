using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.Publishing;

internal interface IPullRequestReviewPublisherProvider
{
    bool CanHandle(string pullRequestUrl);

    Task<bool> PublishReportAsync(
        string pullRequestUrl,
        string accessToken,
        string reportContent,
        CancellationToken cancellationToken);

    Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken);

    Task<bool> PublishInlineCommentAsync(
        string pullRequestUrl,
        string accessToken,
        InlineCommentDraft inlineComment,
        CancellationToken cancellationToken);
}
