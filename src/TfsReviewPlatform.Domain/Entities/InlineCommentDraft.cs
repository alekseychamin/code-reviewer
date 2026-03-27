namespace TfsReviewPlatform.Domain.Entities;

public sealed record InlineCommentDraft(
    Guid Id,
    string FilePath,
    int LineNumber,
    string Title,
    string Severity,
    string Content,
    string ExistingCode,
    string Suggestion,
    string ContextBlock,
    int ContextStartLine,
    int ContextEndLine,
    string RelevantDiffHunk,
    bool PublishedToTfs,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReviewCommentMessage> Messages,
    Guid FindingId = default,
    string Category = "",
    int StartLine = 0,
    int EndLine = 0,
    bool IsRelevant = true);
