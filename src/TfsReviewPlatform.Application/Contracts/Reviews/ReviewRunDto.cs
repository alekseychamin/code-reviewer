using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewRunDto
{
    public Guid Id { get; init; }

    public ReviewRunStatus Status { get; init; }

    public ReviewTargetKind TargetKind { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? PullRequestUrl { get; init; }

    public string? RepositoryName { get; init; }

    public string? SourceBranch { get; init; }

    public string? TargetBranch { get; init; }

    public string? ProviderProfileId { get; init; }

    public string ServiceName { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    public ReviewPipelineStage? CurrentStage { get; init; }

    public int ProgressPercent { get; init; }

    public string CurrentMessage { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string ChangeDescription { get; init; } = string.Empty;

    public ChangeDescriptionStructuredContentDto? ChangeDescriptionStructured { get; init; }

    public string? ChangeDiagramMermaid { get; init; }

    public bool HasDiffArtifact { get; init; }

    public string MarkdownReport { get; init; } = string.Empty;

    public bool HasMarkdownReportArtifact { get; init; }

    public string SummaryComment { get; init; } = string.Empty;

    public bool PublishSucceeded { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<ReviewFindingDto> Findings { get; init; } = [];

    public FindingsComparisonDto? FindingsComparison { get; init; }

    public IReadOnlyList<InlineCommentDto> InlineComments { get; init; } = [];

    public IReadOnlyList<ReviewedFileDto> ReviewedFiles { get; init; } = [];
}
