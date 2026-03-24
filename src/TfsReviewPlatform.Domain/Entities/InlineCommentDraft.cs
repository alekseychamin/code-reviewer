namespace TfsReviewPlatform.Domain.Entities;

public sealed record InlineCommentDraft(
    string FilePath,
    int LineNumber,
    string Content);
