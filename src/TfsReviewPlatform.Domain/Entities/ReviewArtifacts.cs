namespace TfsReviewPlatform.Domain.Entities;

public sealed class ReviewArtifacts
{
    public string DiffText { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string ChangeDescription { get; init; } = string.Empty;

    public string? ChangeDiagramMermaid { get; init; }

    public string MarkdownReport { get; init; } = string.Empty;

    public string SummaryComment { get; init; } = string.Empty;

    public IReadOnlyList<InlineCommentDraft> InlineComments { get; init; } = [];
}
