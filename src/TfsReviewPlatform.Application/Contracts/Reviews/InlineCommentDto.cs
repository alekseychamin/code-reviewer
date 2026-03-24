namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class InlineCommentDto
{
    public Guid Id { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public int LineNumber { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string ExistingCode { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;

    public string ContextBlock { get; init; } = string.Empty;

    public int ContextStartLine { get; init; }

    public int ContextEndLine { get; init; }

    public string RelevantDiffHunk { get; init; } = string.Empty;

    public bool PublishedToTfs { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public IReadOnlyList<ReviewCommentMessageDto> Messages { get; init; } = [];
}
