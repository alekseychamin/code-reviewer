namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class SetInlineCommentRelevanceRequest
{
    public bool IsRelevant { get; init; } = true;
}
