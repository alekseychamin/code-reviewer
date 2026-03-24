namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class InlineCommentDto
{
    public string FilePath { get; init; } = string.Empty;

    public int LineNumber { get; init; }

    public string Content { get; init; } = string.Empty;
}
