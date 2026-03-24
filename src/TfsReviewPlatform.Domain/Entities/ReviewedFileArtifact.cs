namespace TfsReviewPlatform.Domain.Entities;

public sealed class ReviewedFileArtifact
{
    public string FilePath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ChangeType { get; init; } = "Modified";

    public int AddedLines { get; init; }

    public int DeletedLines { get; init; }

    public string DiffPatch { get; init; } = string.Empty;

    public string FullContent { get; init; } = string.Empty;

    public IReadOnlyList<int> ChangedLineNumbers { get; init; } = [];

    public IReadOnlyList<InlineCommentDraft> InlineThreads { get; init; } = [];
}
