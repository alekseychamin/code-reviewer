using TfsReviewPlatform.Application.Abstractions;

namespace TfsReviewPlatform.Integrations.Roslyn;

public sealed class WorkspaceFileSnippetLoader : IGraphChunkContextLoader
{
    public Task<string?> TryLoadSnippetAsync(
        string workspaceRoot,
        string repoRelativeFilePath,
        int centerLine,
        int contextLines,
        CancellationToken cancellationToken)
    {
        if (centerLine <= 0 || string.IsNullOrWhiteSpace(repoRelativeFilePath))
        {
            return Task.FromResult<string?>(null);
        }

        var fullPath = Path.Combine(workspaceRoot, repoRelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<string?>(null);
        }

        var lines = File.ReadAllLines(fullPath);
        if (lines.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var start = Math.Max(1, centerLine - contextLines);
        var end = Math.Min(lines.Length, centerLine + contextLines);
        var snippet = string.Join(
            '\n',
            Enumerable.Range(start, end - start + 1).Select(i => $"{i,4}: {lines[i - 1]}"));
        return Task.FromResult<string?>(snippet);
    }
}
