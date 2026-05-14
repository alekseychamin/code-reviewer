namespace TfsReviewPlatform.Application.Models;

public sealed class ExternalReviewInput
{
    public string DiffText { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? RepositoryName { get; init; }

    public string? ServiceName { get; init; }

    public string? PullRequestTitle { get; init; }

    public string? SourceRef { get; init; }

    public string? TargetRef { get; init; }
}
