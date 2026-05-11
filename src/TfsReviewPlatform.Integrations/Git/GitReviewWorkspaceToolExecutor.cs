using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Tools;

namespace TfsReviewPlatform.Integrations.Git;

public sealed class GitReviewWorkspaceToolExecutor(
    ShellGitCommandRunner gitCommandRunner,
    IRepositoryFileContentService repositoryFileContentService) : IReviewWorkspaceToolExecutor
{
    private const int MaxRequestsPerChunk = 3;
    private const int MaxDeterministicRequests = 24;

    public async Task<IReadOnlyList<ReviewWorkspaceToolResponse>> ExecuteAsync(
        DiffAcquisitionResult diffResult,
        string currentChunkFilePath,
        IReadOnlyList<ReviewWorkspaceToolRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0 ||
            string.IsNullOrWhiteSpace(diffResult.RepositoryPath) ||
            string.IsNullOrWhiteSpace(diffResult.SourceRef))
        {
            return [];
        }

        var supportedRequests = requests
            .Where(IsSupportedRequest)
            .ToArray();
        if (supportedRequests.Length == 0)
        {
            return [];
        }

        var branchFiles = await ListBranchFilesAsync(
            diffResult.RepositoryPath,
            diffResult.SourceRef,
            cancellationToken);

        var maxRequests = string.Equals(currentChunkFilePath, "(deterministic-prefetch)", StringComparison.Ordinal)
            ? MaxDeterministicRequests
            : MaxRequestsPerChunk;

        var normalizedRequests = supportedRequests
            .Let(filtered => SuppressRedundantUseCaseRequests(currentChunkFilePath, filtered))
            .Let(filtered => SuppressSpeculativeInMemoryEnrichmentRequests(currentChunkFilePath, filtered))
            .Let(filtered => SuppressGuessedReadRequestsForTestChunks(currentChunkFilePath, branchFiles, filtered))
            .DistinctBy(request => $"{request.ToolName}|{request.Query}|{request.FilePath}|{request.PathScope}|{request.StartLine}|{request.MaxLines}")
            .Take(maxRequests)
            .ToArray();

        if (normalizedRequests.Length == 0)
        {
            return [];
        }

        var tools = new GitReviewWorkspaceTools(
            gitCommandRunner,
            repositoryFileContentService,
            diffResult.RepositoryPath,
            diffResult.SourceRef,
            diffResult.TargetRef,
            currentChunkFilePath,
            branchFiles);

        var responses = new List<ReviewWorkspaceToolResponse>(normalizedRequests.Length);
        foreach (var request in normalizedRequests)
        {
            var response = await ExecuteRequestAsync(tools, request, cancellationToken);
            responses.Add(response ?? BuildNoResultResponse(request));
        }

        return responses;
    }

    private static async Task<ReviewWorkspaceToolResponse?> ExecuteRequestAsync(
        GitReviewWorkspaceTools tools,
        ReviewWorkspaceToolRequest request,
        CancellationToken cancellationToken)
    {
        return request.ToolName switch
        {
            "find_files" => await tools.FindFilesAsync(
                request.Query,
                request.PathScope,
                maxResults: 12,
                cancellationToken),
            "find_usage" => await tools.FindUsageAsync(
                request.Query,
                request.PathScope,
                maxHits: 20,
                cancellationToken),
            "grep_code" => await tools.GrepCodeAsync(
                request.Query,
                request.PathScope,
                maxHits: 20,
                cancellationToken),
            "read_file" => await tools.ReadFileAsync(
                request.FilePath,
                request.Query,
                request.PathScope,
                request.StartLine,
                request.MaxLines,
                cancellationToken),
            _ => null
        };
    }

    private static ReviewWorkspaceToolResponse BuildNoResultResponse(ReviewWorkspaceToolRequest request)
    {
        var details = new List<string>
        {
            "Workspace tool completed but returned no repository matches."
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            details.Add($"query={request.Query}");
        }
        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            details.Add($"file_path={request.FilePath}");
        }
        if (!string.IsNullOrWhiteSpace(request.PathScope))
        {
            details.Add($"path_scope={request.PathScope}");
        }

        return new ReviewWorkspaceToolResponse(
            request.ToolName,
            "workspace-no-result",
            string.Join(" ", details));
    }

    private async Task<IReadOnlyList<string>> ListBranchFilesAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken)
    {
        var output = await gitCommandRunner.RunAsync(
            repositoryPath,
            ["ls-tree", "-r", "--name-only", revision],
            cancellationToken);

        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path =>
                !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSupportedRequest(ReviewWorkspaceToolRequest request)
    {
        return request.ToolName is "find_files" or "find_usage" or "grep_code" or "read_file";
    }

    private static IEnumerable<ReviewWorkspaceToolRequest> SuppressRedundantUseCaseRequests(
        string currentChunkFilePath,
        IEnumerable<ReviewWorkspaceToolRequest> requests)
    {
        var requestArray = requests.ToArray();
        if (requestArray.Length == 0)
        {
            return requestArray;
        }

        var isSqlOrUseCaseChunk =
            currentChunkFilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
            requestArray.Any(request => LooksLikeUseCaseQuery(request.Query));
        if (!isSqlOrUseCaseChunk)
        {
            return requestArray;
        }

        var findUsageRequests = requestArray
            .Where(request => string.Equals(request.ToolName, "find_usage", StringComparison.Ordinal))
            .ToArray();
        if (findUsageRequests.Length == 0)
        {
            return requestArray;
        }

        return requestArray.Where(request =>
        {
            if (!string.Equals(request.ToolName, "grep_code", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(request.FilePath))
            {
                return true;
            }

            return findUsageRequests.Any(findUsage =>
                RefinesSameUseCaseLocator(findUsage.Query, request.Query));
        });
    }

    private static bool RefinesSameUseCaseLocator(string baseQuery, string candidateQuery)
    {
        var normalizedBase = (baseQuery ?? string.Empty).Trim();
        var normalizedCandidate = (candidateQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedBase) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return false;
        }

        return normalizedBase.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.Contains(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUseCaseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        return query.StartsWith("Get", StringComparison.Ordinal) ||
               query.StartsWith("Create", StringComparison.Ordinal) ||
               query.StartsWith("Update", StringComparison.Ordinal) ||
               query.StartsWith("Delete", StringComparison.Ordinal) ||
               query.StartsWith("List", StringComparison.Ordinal) ||
               query.StartsWith("Find", StringComparison.Ordinal);
    }

    private static IEnumerable<ReviewWorkspaceToolRequest> SuppressGuessedReadRequestsForTestChunks(
        string currentChunkFilePath,
        IReadOnlyList<string> branchFiles,
        IEnumerable<ReviewWorkspaceToolRequest> requests)
    {
        var requestArray = requests.ToArray();
        if (requestArray.Length == 0 || !IsTestChunk(currentChunkFilePath))
        {
            return requestArray;
        }

        var hasReliableDirectContext = requestArray.Any(request =>
            string.Equals(request.ToolName, "find_usage", StringComparison.Ordinal) ||
            (string.Equals(request.ToolName, "read_file", StringComparison.Ordinal) &&
             HasExactFilePath(branchFiles, request.FilePath)));
        if (!hasReliableDirectContext)
        {
            return requestArray;
        }

        return requestArray.Where(request =>
        {
            if (!string.Equals(request.ToolName, "read_file", StringComparison.Ordinal))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(request.FilePath) ||
                   HasExactFilePath(branchFiles, request.FilePath);
        });
    }

    private static IEnumerable<ReviewWorkspaceToolRequest> SuppressSpeculativeInMemoryEnrichmentRequests(
        string currentChunkFilePath,
        IEnumerable<ReviewWorkspaceToolRequest> requests)
    {
        var requestArray = requests.ToArray();
        if (requestArray.Length == 0 || !LooksLikeInMemoryEnrichmentChunk(currentChunkFilePath))
        {
            return requestArray;
        }

        return requestArray.Where(request => !LooksLikeSpeculativeCostProbe(request));
    }

    private static bool IsTestChunk(string currentChunkFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentChunkFilePath))
        {
            return false;
        }

        return currentChunkFilePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.Contains("\\tests\\", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.Contains("/testcontainerstests/", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.Contains("\\testcontainerstests\\", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInMemoryEnrichmentChunk(string currentChunkFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentChunkFilePath))
        {
            return false;
        }

        return currentChunkFilePath.EndsWith("Extensions.cs", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.Contains("Enrich", StringComparison.OrdinalIgnoreCase) ||
               currentChunkFilePath.Contains("CacheExtensions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSpeculativeCostProbe(ReviewWorkspaceToolRequest request)
    {
        var text = $"{request.Query} {request.FilePath} {request.Reason}";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var probesHiddenCost =
            text.Contains("N+1", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("дорогостоящ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("дорог", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("сетев", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("expensive", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("external i/o", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("remote work", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("удален", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("внешн", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("perform external", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("производительност", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("актуальна ли проблема", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("типичный размер", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("быстрые in-memory операции", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("быстрые операции", StringComparison.OrdinalIgnoreCase);

        if (!probesHiddenCost)
        {
            return false;
        }

        return string.Equals(request.ToolName, "find_usage", StringComparison.Ordinal) ||
               string.Equals(request.ToolName, "find_files", StringComparison.Ordinal) ||
               (string.Equals(request.ToolName, "read_file", StringComparison.Ordinal) &&
                request.FilePath.Contains("Abstractions/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExactFilePath(IReadOnlyList<string> branchFiles, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalizedPath = filePath.Trim().TrimStart('/').Replace('\\', '/');
        return branchFiles.Any(path => string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase));
    }
}

file static class LinqExtensions
{
    public static TResult Let<TSource, TResult>(this TSource source, Func<TSource, TResult> selector)
    {
        return selector(source);
    }
}
