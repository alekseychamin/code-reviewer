using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Integrations.Git;

public sealed class BranchComparisonDiffService(ShellGitCommandRunner gitCommandRunner)
    : IBranchComparisonDiffService
{
    public async Task<DiffAcquisitionResult> GetDiffAsync(
        string repositoryPath,
        string targetBranch,
        string sourceBranch,
        string? repositoryName,
        CancellationToken cancellationToken)
    {
        var diffText = await gitCommandRunner.RunAsync(
            repositoryPath,
            ["diff", $"{targetBranch}...{sourceBranch}"],
            cancellationToken);

        var authorName = await TryGetLatestAuthorAsync(repositoryPath, sourceBranch, cancellationToken);
        var resolvedRepositoryName = repositoryName ?? Path.GetFileName(repositoryPath);

        return new DiffAcquisitionResult
        {
            DiffText = diffText,
            RepositoryPath = repositoryPath,
            RepositoryName = resolvedRepositoryName,
            ServiceName = resolvedRepositoryName,
            AuthorName = authorName,
            SourceRef = sourceBranch,
            TargetRef = targetBranch,
            GitFetchSourceRef = sourceBranch,
            GitFetchTargetRef = targetBranch
        };
    }

    private async Task<string?> TryGetLatestAuthorAsync(
        string repositoryPath,
        string sourceBranch,
        CancellationToken cancellationToken)
    {
        try
        {
            var author = await gitCommandRunner.RunAsync(
                repositoryPath,
                ["log", "-1", "--format=%an <%ae>", sourceBranch],
                cancellationToken);

            var trimmed = author.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
        catch
        {
            return null;
        }
    }
}
