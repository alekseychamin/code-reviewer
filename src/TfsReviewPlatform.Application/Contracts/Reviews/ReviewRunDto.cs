using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewRunDto
{
    public Guid Id { get; init; }

    public ReviewRunStatus Status { get; init; }

    public ReviewTargetKind TargetKind { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? ProviderProfileId { get; init; }

    public bool LocalOnlyMode { get; init; }

    public ReviewPipelineStage? CurrentStage { get; init; }

    public int ProgressPercent { get; init; }

    public string CurrentMessage { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string ChangeDescription { get; init; } = string.Empty;

    public string MarkdownReport { get; init; } = string.Empty;

    public string SummaryComment { get; init; } = string.Empty;

    public bool PublishSucceeded { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<ReviewFindingDto> Findings { get; init; } = [];

    public IReadOnlyList<InlineCommentDto> InlineComments { get; init; } = [];
}
