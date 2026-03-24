using TfsReviewPlatform.Application.Abstractions;

namespace TfsReviewPlatform.Integrations.Git;

public sealed class GitRepositoryFileContentService(ShellGitCommandRunner gitCommandRunner) : IRepositoryFileContentService
{
    public async Task<string?> TryGetFileContentAsync(
        string repositoryPath,
        string revision,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) ||
            string.IsNullOrWhiteSpace(revision) ||
            string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return await gitCommandRunner.RunAsync(
                repositoryPath,
                ["show", $"{revision}:{filePath.TrimStart('/')}"],
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
