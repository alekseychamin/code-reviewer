using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IBranchComparisonDiffService
{
    Task<DiffAcquisitionResult> GetDiffAsync(
        string repositoryPath,
        string targetBranch,
        string sourceBranch,
        string? repositoryName,
        CancellationToken cancellationToken);
}
