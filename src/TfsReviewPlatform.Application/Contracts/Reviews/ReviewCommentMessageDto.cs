namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewCommentMessageDto
{
    public string Role { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public InlineDiscussionStructuredContentDto? StructuredContent { get; init; }
}
