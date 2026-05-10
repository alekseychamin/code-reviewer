using TfsReviewPlatform.Application.Models.Graph;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IRoslynGraphBuilder
{
    Task<CodeGraph?> BuildAsync(
        string workspaceDirectory,
        string solutionPath,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken);
}
