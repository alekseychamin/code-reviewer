using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewWorkspaceToolExecutor
{
    Task<IReadOnlyList<ReviewWorkspaceToolResponse>> ExecuteAsync(
        DiffAcquisitionResult diffResult,
        string currentChunkFilePath,
        IReadOnlyList<ReviewWorkspaceToolRequest> requests,
        CancellationToken cancellationToken);
}
