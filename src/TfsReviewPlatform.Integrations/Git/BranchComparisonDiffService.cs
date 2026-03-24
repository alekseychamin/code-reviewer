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

        return new DiffAcquisitionResult
        {
            DiffText = diffText,
            RepositoryPath = repositoryPath,
            RepositoryName = repositoryName ?? Path.GetFileName(repositoryPath),
            SourceRef = sourceBranch,
            TargetRef = targetBranch
        };
    }
}
