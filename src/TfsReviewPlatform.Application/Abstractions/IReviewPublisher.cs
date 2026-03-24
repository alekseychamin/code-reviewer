using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewPublisher
{
    Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken);
}
