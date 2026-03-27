namespace TfsReviewPlatform.Domain.Entities;

public sealed class ReviewArtifacts
{
    public string DiffText { get; init; } = string.Empty;

    public IReadOnlyList<string> PreparedChunks { get; init; } = [];

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string ChangeDescription { get; init; } = string.Empty;

    public ChangeDescriptionStructuredContent? ChangeDescriptionStructured { get; init; }

    public string? ChangeDiagramMermaid { get; init; }

    public string MarkdownReport { get; init; } = string.Empty;

    public string SummaryComment { get; init; } = string.Empty;

    public IReadOnlyList<ReviewCommentMessage> ReviewDiscussionMessages { get; init; } = [];

    public IReadOnlyList<InlineCommentDraft> InlineComments { get; init; } = [];

    public IReadOnlyList<ReviewedFileArtifact> ReviewedFiles { get; init; } = [];

    public FindingsComparisonSnapshot? FindingsComparison { get; init; }
}
