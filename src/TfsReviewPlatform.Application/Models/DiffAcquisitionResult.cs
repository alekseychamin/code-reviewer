namespace TfsReviewPlatform.Application.Models;

public sealed class DiffAcquisitionResult
{
    public required string DiffText { get; init; }

    public required string RepositoryName { get; init; }

    public string? PullRequestTitle { get; init; }

    public string ServiceName { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    public string? RepositoryPath { get; init; }

    public string? PullRequestUrl { get; init; }

    public string? SourceRef { get; init; }

    public string? TargetRef { get; init; }

    public string? CleanupDirectory { get; init; }

    /// <summary>HTTPS remote URL used to clone a full working tree for Roslyn (pull requests).</summary>
    public string? RepositoryRemoteUrl { get; init; }

    /// <summary>Source ref passed to <c>git fetch origin {spec}:refs/remotes/origin/__source__</c> (API ref or branch name).</summary>
    public string? GitFetchSourceRef { get; init; }

    /// <summary>Target ref passed to <c>git fetch origin {spec}:refs/remotes/origin/__target__</c>.</summary>
    public string? GitFetchTargetRef { get; init; }

    /// <summary>Value for <c>git -c http.extraHeader=...</c> when cloning/fetching (pull requests with PAT).</summary>
    public string? GitHttpExtraHeader { get; init; }
}
