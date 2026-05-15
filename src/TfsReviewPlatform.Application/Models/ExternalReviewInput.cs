namespace TfsReviewPlatform.Application.Models;

public sealed class ExternalReviewInput
{
    public string DiffText { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string RiskDomainsSummary { get; init; } = string.Empty;

    public string? RepositoryName { get; init; }

    public string? ServiceName { get; init; }

    public string? PullRequestTitle { get; init; }

    public string? PullRequestUrl { get; init; }

    public string? RepositoryPath { get; init; }

    public string? RepositoryRemoteUrl { get; init; }

    public string? GitFetchSourceRef { get; init; }

    public string? GitFetchTargetRef { get; init; }

    public string? GitHttpExtraHeader { get; init; }

    public string? SourceRef { get; init; }

    public string? TargetRef { get; init; }
}
