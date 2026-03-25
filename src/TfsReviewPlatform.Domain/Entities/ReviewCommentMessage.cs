namespace TfsReviewPlatform.Domain.Entities;

public sealed record ReviewCommentMessage(
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    InlineDiscussionStructuredContent? StructuredContent = null);
