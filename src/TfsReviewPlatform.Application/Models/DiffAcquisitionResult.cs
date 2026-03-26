namespace TfsReviewPlatform.Application.Models;

public sealed class DiffAcquisitionResult
{
    public required string DiffText { get; init; }

    public required string RepositoryName { get; init; }

    public string ServiceName { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    public string? RepositoryPath { get; init; }

    public string? PullRequestUrl { get; init; }

    public string? SourceRef { get; init; }

    public string? TargetRef { get; init; }

    public string? CleanupDirectory { get; init; }
}
