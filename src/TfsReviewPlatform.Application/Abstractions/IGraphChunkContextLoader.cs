namespace TfsReviewPlatform.Application.Abstractions;

public interface IGraphChunkContextLoader
{
    Task<string?> TryLoadSnippetAsync(
        string workspaceRoot,
        string repoRelativeFilePath,
        int centerLine,
        int contextLines,
        CancellationToken cancellationToken);
}
