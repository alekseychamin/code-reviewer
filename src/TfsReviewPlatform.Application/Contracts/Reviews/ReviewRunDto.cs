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

    public IReadOnlyList<ReviewOpportunityItemDto> PrimaryOpportunities { get; init; } = [];

    public FindingsComparisonDto? FindingsComparison { get; init; }

    public SemanticCodeContextDto SemanticCodeContext { get; init; } = new();

    public ExternalReviewDto ExternalReview { get; init; } = new();

    public IReadOnlyList<ReviewCommentMessageDto> ReviewDiscussionMessages { get; init; } = [];

    public IReadOnlyList<InlineCommentDto> InlineComments { get; init; } = [];

    public IReadOnlyList<ReviewedFileDto> ReviewedFiles { get; init; } = [];

    public IReadOnlyList<ReviewProgressUpdateDto> ProgressUpdates { get; init; } = [];
}

public sealed class ExternalReviewDto
{
    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public bool TimedOut { get; init; }

    public string EngineName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public int ElapsedMilliseconds { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public IReadOnlyList<ExternalReviewCommandDto> Commands { get; init; } = [];
}

public sealed class ExternalReviewCommandDto
{
    public string Command { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public int ElapsedMilliseconds { get; init; }

    public string Artifact { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}

public sealed class ReviewProgressUpdateDto
{
    public Guid RunId { get; init; }

    public ReviewRunStatus Status { get; init; }

    public ReviewPipelineStage? Stage { get; init; }

    public int ProgressPercent { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; }

    public bool IsTerminal { get; init; }
}

public sealed class SemanticCodeContextDto
{
    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public bool TimedOut { get; init; }

    public bool CacheReuseEnabled { get; init; }

    public bool SourceCacheHit { get; init; }

    public bool TargetCacheHit { get; init; }

    public int SourceFilesSelected { get; init; }

    public int TargetFilesSelected { get; init; }

    public int SourceFilesIndexed { get; init; }

    public int TargetFilesIndexed { get; init; }

    public int SourceChunksIndexed { get; init; }

    public int TargetChunksIndexed { get; init; }

    public int SourceFilesMissing { get; init; }

    public int TargetFilesMissing { get; init; }

    public int SourceFilesTooLarge { get; init; }

    public int TargetFilesTooLarge { get; init; }

    public int SourceFilesEmpty { get; init; }

    public int TargetFilesEmpty { get; init; }

    public int SourceFilesWithoutChunks { get; init; }

    public int TargetFilesWithoutChunks { get; init; }

    public int SourceFilesReadFailed { get; init; }

    public int TargetFilesReadFailed { get; init; }

    public int SourceFilesSkippedDeleted { get; init; }

    public int TargetFilesSkippedAdded { get; init; }

    public int TargetBaselineFilesSelected { get; init; }

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int SnippetCount { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? SourceCommitSha { get; init; }

    public string? TargetCommitSha { get; init; }

    public IReadOnlyList<SemanticCodeContextSnippetDto> Snippets { get; init; } = [];
}

public sealed class SemanticCodeContextSnippetDto
{
    public string RevisionKind { get; init; } = string.Empty;

    public string CommitSha { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public double Score { get; init; }

    public string Query { get; init; } = string.Empty;
}
