using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public sealed record RoslynWorkspaceBootstrapResult(
    bool Success,
    string? WorkspaceDirectory,
    string? SolutionPath,
    string? ChangedFilesPath,
    string? CleanupDirectory,
    string? ErrorMessage);

public interface IRoslynWorkspaceBootstrapper
{
    Task<RoslynWorkspaceBootstrapResult> TryPrepareAsync(
        Guid runId,
        DiffAcquisitionResult diffResult,
        string? pullRequestAccessToken,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken);
}
