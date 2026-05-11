using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewHistoryItemDto
{
    public Guid Id { get; init; }

    public ReviewRunStatus Status { get; init; }

    public ReviewTargetKind TargetKind { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? PullRequestUrl { get; init; }

    public string? RepositoryName { get; init; }

    public string? SourceBranch { get; init; }

    public string? TargetBranch { get; init; }

    public string ServiceName { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    public string? ProviderProfileId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int FindingsCount { get; init; }

    public int CriticalCount { get; init; }

    public int HighCount { get; init; }

    public bool PublishSucceeded { get; init; }

    public bool HasMarkdownReportArtifact { get; init; }
}
