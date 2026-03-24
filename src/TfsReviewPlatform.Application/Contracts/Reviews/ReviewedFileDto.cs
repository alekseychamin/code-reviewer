namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewedFileDto
{
    public string FilePath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ChangeType { get; init; } = string.Empty;

    public int AddedLines { get; init; }

    public int DeletedLines { get; init; }

    public string DiffPatch { get; init; } = string.Empty;

    public string FullContent { get; init; } = string.Empty;

    public IReadOnlyList<int> ChangedLineNumbers { get; init; } = [];

    public IReadOnlyList<InlineCommentDto> InlineThreads { get; init; } = [];
}
