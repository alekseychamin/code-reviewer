using System.ComponentModel;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;

namespace TfsReviewPlatform.Integrations.Tools;

public sealed class GitReviewWorkspaceTools(
    ShellGitCommandRunner gitCommandRunner,
    IRepositoryFileContentService repositoryFileContentService,
    string repositoryPath,
    string sourceRef,
    string? targetRef,
    string currentChunkFilePath,
    IReadOnlyList<string> branchFiles)
{
    private const int MaxToolOutputLength = 12000;
    private const int MaxReadLines = 300;
    private const int DefaultMaxListedFiles = 12;
    private const int DefaultMaxGrepHits = 20;
    private sealed record ContentHitScore(string FilePath, int Score);
    private sealed record UsageHitScore(string FilePath, int LineNumber, string Code, int Score);

    [Description("Найти релевантные файлы в текущей ветке по части имени файла, символу или фиче.")]
    public async Task<ReviewWorkspaceToolResponse?> FindFilesAsync(
        [Description("Короткий запрос: часть имени файла, имя класса, метода, SQL-фрагмент или фича")] string query,
        [Description("Необязательное ограничение по папке или feature area")] string pathScope = "",
        [Description("Максимум файлов в ответе")] int maxResults = DefaultMaxListedFiles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return null;
        }

        var effectiveMaxResults = Math.Clamp(maxResults, 1, DefaultMaxListedFiles);
        var scopedFiles = ApplyPathScope(branchFiles, pathScope);
        var grepHitScores = await FindFilesByContentAsync(trimmedQuery, pathScope, cancellationToken);
        var matches = scopedFiles
            .Select(path => new
            {
                Path = path,
                Score = ScoreFileSearchCandidate(currentChunkFilePath, trimmedQuery, pathScope, path) +
                        ScoreContentHit(path, grepHitScores)
            })
            .Where(item => item.Score >= 25)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .Take(effectiveMaxResults)
            .Select(item => item.Path)
            .ToArray();

        if (matches.Length == 0)
        {
            return null;
        }

        var content = string.Join('\n', matches.Select(path => $"- {path}"));
        return new ReviewWorkspaceToolResponse(
            "find_files",
            grepHitScores.Count > 0 ? "git-ls-tree+git-grep" : "git-ls-tree",
            content,
            matches[0]);
    }

    [Description("Найти строки кода, SQL или конфигурации по точному или почти точному фрагменту.")]
    public async Task<ReviewWorkspaceToolResponse?> GrepCodeAsync(
        [Description("Символ, SQL-фрагмент, имя метода, класса или ключ конфигурации")] string query,
        [Description("Необязательное ограничение по папке или feature area")] string pathScope = "",
        [Description("Максимум совпадений в ответе")] int maxHits = DefaultMaxGrepHits,
        CancellationToken cancellationToken = default)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return null;
        }

        var arguments = new List<string>
        {
            "grep",
            "-n",
            "-I",
            "--full-name",
            "-e",
            trimmedQuery,
            sourceRef
        };

        var normalizedScope = NormalizePathScope(pathScope);
        if (!string.IsNullOrWhiteSpace(normalizedScope))
        {
            arguments.Add("--");
            arguments.Add(normalizedScope);
        }

        try
        {
            var output = await gitCommandRunner.RunAsync(
                repositoryPath,
                arguments,
                cancellationToken);

            var effectiveMaxHits = Math.Clamp(maxHits, 1, DefaultMaxGrepHits);
            var lines = output
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => NormalizeGrepLine(line, sourceRef))
                .Take(effectiveMaxHits)
                .ToArray();
            if (lines.Length == 0)
            {
                return null;
            }

            var firstMatch = lines[0].Split(':', 3);
            var filePath = firstMatch.Length > 0 ? firstMatch[0] : string.Empty;
            var startLine = firstMatch.Length > 1 && int.TryParse(firstMatch[1], out var parsedLine)
                ? parsedLine
                : 0;

            return new ReviewWorkspaceToolResponse(
                "grep_code",
                "git-grep",
                TrimToolOutput(string.Join('\n', lines)),
                filePath,
                startLine,
                startLine);
        }
        catch
        {
            return null;
        }
    }

    [Description("Найти места использования символа, query-like метода или SQL use-case в текущей ветке.")]
    public async Task<ReviewWorkspaceToolResponse?> FindUsageAsync(
        [Description("Символ, имя метода, query/use-case имя или SQL locator")] string query,
        [Description("Необязательное ограничение по папке или feature area")] string pathScope = "",
        [Description("Максимум совпадений в ответе")] int maxHits = DefaultMaxGrepHits,
        CancellationToken cancellationToken = default)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return null;
        }

        var usageHits = await FindUsageHitsAsync(trimmedQuery, pathScope, cancellationToken);
        if (usageHits.Count == 0)
        {
            return null;
        }

        var effectiveMaxHits = Math.Clamp(maxHits, 1, DefaultMaxGrepHits);
        var selectedHits = usageHits
            .Take(effectiveMaxHits)
            .ToArray();

        var firstMatch = selectedHits[0];
        var content = string.Join(
            '\n',
            selectedHits.Select(hit => $"{hit.FilePath}:{hit.LineNumber}:{hit.Code}"));

        return new ReviewWorkspaceToolResponse(
            "find_usage",
            "git-grep-usage",
            TrimToolOutput(content),
            firstMatch.FilePath,
            firstMatch.LineNumber,
            firstMatch.LineNumber);
    }

    [Description("Прочитать фрагмент файла из текущей ветки, если путь известен или почти известен.")]
    public async Task<ReviewWorkspaceToolResponse?> ReadFileAsync(
        [Description("Repository-relative путь к файлу, если он уже известен")] string filePath = "",
        [Description("Дополнительный поисковый фрагмент, если точный путь неизвестен")] string query = "",
        [Description("Необязательное ограничение по папке или feature area")] string pathScope = "",
        [Description("Стартовая строка, если нужен конкретный участок")] int startLine = 1,
        [Description("Максимум строк для чтения")] int maxLines = 120,
        CancellationToken cancellationToken = default)
    {
        var request = new ReviewWorkspaceToolRequest(
            "read_file",
            string.Empty,
            query,
            filePath,
            pathScope,
            startLine,
            maxLines);

        var resolvedPath = ResolveRequestedFilePath(currentChunkFilePath, request, branchFiles);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        var content = await repositoryFileContentService.TryGetFileContentAsync(
            repositoryPath,
            sourceRef,
            resolvedPath,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(targetRef))
        {
            content = await repositoryFileContentService.TryGetFileContentAsync(
                repositoryPath,
                targetRef,
                resolvedPath,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var effectiveMaxLines = Math.Clamp(maxLines, 20, MaxReadLines);
        var effectiveStartLine = Math.Max(1, startLine);
        var excerpt = ExtractFileExcerpt(content, effectiveStartLine, effectiveMaxLines);

        return new ReviewWorkspaceToolResponse(
            "read_file",
            "git-show",
            excerpt.Content,
            resolvedPath,
            excerpt.StartLine,
            excerpt.EndLine);
    }

    private static IReadOnlyList<string> ApplyPathScope(IReadOnlyList<string> files, string pathScope)
    {
        var normalizedScope = NormalizePathScope(pathScope);
        if (string.IsNullOrWhiteSpace(normalizedScope))
        {
            return files;
        }

        var scoped = files
            .Where(path => path.Contains(normalizedScope, StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith(normalizedScope, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return scoped.Length > 0 ? scoped : files;
    }

    private static string ResolveRequestedFilePath(
        string currentChunkFilePath,
        ReviewWorkspaceToolRequest request,
        IReadOnlyList<string> files)
    {
        var scopedFiles = ApplyPathScope(files, request.PathScope);
        var requestedPath = request.FilePath.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            var queryMatch = scopedFiles
                .Select(path => new
                {
                    Path = path,
                    Score = ScoreFileSearchCandidate(currentChunkFilePath, request.Query, request.PathScope, path)
                })
                .Where(item => item.Score >= 25)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .FirstOrDefault();

            return queryMatch?.Path ?? string.Empty;
        }

        var exactMatch = scopedFiles.FirstOrDefault(path =>
            string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactMatch))
        {
            return exactMatch;
        }

        var requestedFileName = Path.GetFileName(requestedPath);
        var basenameMatches = scopedFiles
            .Where(path => string.Equals(Path.GetFileName(path), requestedFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (basenameMatches.Length == 1)
        {
            return basenameMatches[0];
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var queryMatch = scopedFiles
                .Select(path => new
                {
                    Path = path,
                    Score = ScoreFileSearchCandidate(currentChunkFilePath, request.Query, request.PathScope, path)
                })
                .Where(item => item.Score >= 40)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .Select(item => item.Path)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(queryMatch))
            {
                return queryMatch;
            }
        }

        return scopedFiles
            .Select(path => new
            {
                Path = path,
                Score = ScorePathCandidate(currentChunkFilePath, requestedPath, request, path)
            })
            .Where(item => item.Score >= 35)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .Select(item => item.Path)
            .FirstOrDefault() ?? string.Empty;
    }

    private static int ScoreFileSearchCandidate(
        string currentChunkFilePath,
        string query,
        string pathScope,
        string candidatePath)
    {
        var score = 0;
        var trimmedQuery = query.Trim();
        var looksLikeSymbol = LooksLikeSymbolQuery(trimmedQuery);
        var looksLikeUseCase = LooksLikeUseCaseQuery(trimmedQuery);
        if (!string.IsNullOrWhiteSpace(trimmedQuery))
        {
            var queryTokens = Tokenize(trimmedQuery);
            var pathTokens = Tokenize(candidatePath);
            score += queryTokens.Intersect(pathTokens, StringComparer.OrdinalIgnoreCase).Count() * 8;

            var fileName = Path.GetFileName(candidatePath);
            var baseName = Path.GetFileNameWithoutExtension(candidatePath);
            if (string.Equals(fileName, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 180;
            }
            else if (string.Equals(baseName, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 150;
            }
            else if (fileName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 90;
            }
            else if (baseName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
            }

            if (candidatePath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
        }

        if (!string.IsNullOrWhiteSpace(pathScope))
        {
            var normalizedScope = NormalizePathScope(pathScope);
            if (candidatePath.Contains(normalizedScope, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentChunkFilePath))
        {
            var currentDir = Path.GetDirectoryName(currentChunkFilePath.Replace('\\', '/'));
            var candidateDir = Path.GetDirectoryName(candidatePath.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(currentDir) &&
                !string.IsNullOrWhiteSpace(candidateDir) &&
                string.Equals(currentDir, candidateDir, StringComparison.OrdinalIgnoreCase))
            {
                score += looksLikeSymbol ? 10 : 40;
            }
        }

        if (looksLikeUseCase)
        {
            if (candidatePath.Contains("/Contracts/", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.Contains("/Requests/", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase))
            {
                score -= 140;
            }

            if (candidatePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                score -= 90;
            }

            if (candidatePath.Contains("/Providers/", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.Contains("/Handlers/", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.Contains("/Repositories/", StringComparison.OrdinalIgnoreCase) ||
                candidatePath.EndsWith("Context.cs", StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
            }
        }

        return score;
    }

    private async Task<IReadOnlyList<ContentHitScore>> FindFilesByContentAsync(
        string query,
        string pathScope,
        CancellationToken cancellationToken)
    {
        var scopedMatches = await GrepFilePathsAsync(query, pathScope, cancellationToken);
        if (scopedMatches.Count > 0 || string.IsNullOrWhiteSpace(pathScope))
        {
            return scopedMatches;
        }

        return await GrepFilePathsAsync(query, string.Empty, cancellationToken);
    }

    private async Task<IReadOnlyList<UsageHitScore>> FindUsageHitsAsync(
        string query,
        string pathScope,
        CancellationToken cancellationToken)
    {
        var scopedHits = await GrepUsageHitsAsync(query, pathScope, cancellationToken);
        if (scopedHits.Count > 0 || string.IsNullOrWhiteSpace(pathScope))
        {
            return scopedHits;
        }

        return await GrepUsageHitsAsync(query, string.Empty, cancellationToken);
    }

    private async Task<IReadOnlyList<ContentHitScore>> GrepFilePathsAsync(
        string query,
        string pathScope,
        CancellationToken cancellationToken)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return [];
        }

        var arguments = new List<string>
        {
            "grep",
            "-n",
            "-I",
            "-I",
            "-e",
            trimmedQuery,
            sourceRef
        };

        var normalizedScope = NormalizePathScope(pathScope);
        if (!string.IsNullOrWhiteSpace(normalizedScope))
        {
            arguments.Add("--");
            arguments.Add(normalizedScope);
        }

        try
        {
            var output = await gitCommandRunner.RunAsync(
                repositoryPath,
                arguments,
                cancellationToken);

            return output
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => NormalizeGrepLine(line, sourceRef))
                .Select(line => ScoreContentHitLine(trimmedQuery, line))
                .Where(item => item is not null)
                .Cast<ContentHitScore>()
                .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ContentHitScore(group.Key, group.Max(item => item.Score)))
                .OrderByDescending(item => item.Score)
                .Take(DefaultMaxListedFiles)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<UsageHitScore>> GrepUsageHitsAsync(
        string query,
        string pathScope,
        CancellationToken cancellationToken)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return [];
        }

        var arguments = new List<string>
        {
            "grep",
            "-n",
            "-I",
            "-e",
            trimmedQuery,
            sourceRef
        };

        var normalizedScope = NormalizePathScope(pathScope);
        if (!string.IsNullOrWhiteSpace(normalizedScope))
        {
            arguments.Add("--");
            arguments.Add(normalizedScope);
        }

        try
        {
            var output = await gitCommandRunner.RunAsync(
                repositoryPath,
                arguments,
                cancellationToken);

            return output
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => NormalizeGrepLine(line, sourceRef))
                .Select(line => ScoreUsageHitLine(currentChunkFilePath, trimmedQuery, pathScope, line))
                .Where(item => item is not null)
                .Cast<UsageHitScore>()
                .Where(item => item.Score >= 40)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.FilePath.Length)
                .ThenBy(item => item.LineNumber)
                .Take(DefaultMaxGrepHits)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static int ScoreContentHit(string candidatePath, IReadOnlyList<ContentHitScore> grepHitFiles)
    {
        for (var index = 0; index < grepHitFiles.Count; index++)
        {
            if (string.Equals(candidatePath, grepHitFiles[index].FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return grepHitFiles[index].Score - (index * 10);
            }
        }

        return 0;
    }

    private static ContentHitScore? ScoreContentHitLine(string query, string line)
    {
        var parts = line.Split(':', 3);
        if (parts.Length < 3)
        {
            return null;
        }

        var filePath = parts[0].Trim();
        var code = parts[2].Trim();
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var score = 120;
        if (LooksLikeDefinitionLine(query, code))
        {
            score += 220;
        }
        else if (code.Contains(query, StringComparison.Ordinal))
        {
            score += 80;
        }

        return new ContentHitScore(filePath, score);
    }

    private static UsageHitScore? ScoreUsageHitLine(
        string currentChunkFilePath,
        string query,
        string pathScope,
        string line)
    {
        var parts = line.Split(':', 3);
        if (parts.Length < 3)
        {
            return null;
        }

        var filePath = parts[0].Trim();
        var lineNumberText = parts[1].Trim();
        var code = parts[2].Trim();
        if (string.IsNullOrWhiteSpace(filePath) ||
            string.IsNullOrWhiteSpace(code) ||
            !int.TryParse(lineNumberText, out var lineNumber))
        {
            return null;
        }

        var score = 100;
        var looksLikeUseCase = LooksLikeUseCaseQuery(query);
        var looksLikeSymbol = LooksLikeSymbolQuery(query);
        var isDefinition = LooksLikeDefinitionLine(query, code);

        if (code.Contains(query, StringComparison.Ordinal))
        {
            score += 40;
        }

        if (looksLikeUseCase && code.Contains($"{query}(", StringComparison.Ordinal))
        {
            score += 80;
        }

        if (isDefinition)
        {
            score -= looksLikeUseCase ? 180 : 120;
        }

        if (!string.IsNullOrWhiteSpace(pathScope))
        {
            var normalizedScope = NormalizePathScope(pathScope);
            if (filePath.Contains(normalizedScope, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentChunkFilePath))
        {
            var currentDir = Path.GetDirectoryName(currentChunkFilePath.Replace('\\', '/'));
            var candidateDir = Path.GetDirectoryName(filePath.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(currentDir) &&
                !string.IsNullOrWhiteSpace(candidateDir) &&
                string.Equals(currentDir, candidateDir, StringComparison.OrdinalIgnoreCase))
            {
                score += looksLikeUseCase ? 10 : 25;
            }
        }

        if (looksLikeUseCase)
        {
            if (filePath.Contains("/Providers/", StringComparison.OrdinalIgnoreCase) ||
                filePath.Contains("/Handlers/", StringComparison.OrdinalIgnoreCase) ||
                filePath.Contains("/Repositories/", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith("Context.cs", StringComparison.OrdinalIgnoreCase) ||
                filePath.Contains("/Infrastructure/", StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            if (filePath.Contains("/Contracts/", StringComparison.OrdinalIgnoreCase) ||
                filePath.Contains("/Requests/", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                score -= 120;
            }
        }
        else if (looksLikeSymbol)
        {
            if (code.Contains($"{query}(", StringComparison.Ordinal) ||
                code.Contains($"new {query}", StringComparison.Ordinal) ||
                code.Contains($"{query}.", StringComparison.Ordinal))
            {
                score += 60;
            }
        }

        return new UsageHitScore(filePath, lineNumber, code, score);
    }

    private static bool LooksLikeDefinitionLine(string query, string code)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return code.Contains($"class {query}", StringComparison.Ordinal) ||
               code.Contains($"interface {query}", StringComparison.Ordinal) ||
               code.Contains($"record {query}", StringComparison.Ordinal) ||
               (code.Contains($"{query}(", StringComparison.Ordinal) &&
                (code.Contains("public ", StringComparison.Ordinal) ||
                 code.Contains("private ", StringComparison.Ordinal) ||
                 code.Contains("protected ", StringComparison.Ordinal) ||
                 code.Contains("internal ", StringComparison.Ordinal) ||
                 code.Contains("static ", StringComparison.Ordinal)));
    }

    private static bool LooksLikeSymbolQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        return char.IsUpper(query[0]) &&
               !query.Contains('.', StringComparison.Ordinal) &&
               !query.Contains(' ', StringComparison.Ordinal);
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

    private static string NormalizeGrepLine(string line, string sourceRef)
    {
        var prefix = sourceRef + ":";
        return line.StartsWith(prefix, StringComparison.Ordinal)
            ? line[prefix.Length..]
            : line;
    }

    private static int ScorePathCandidate(
        string currentChunkFilePath,
        string requestedPath,
        ReviewWorkspaceToolRequest request,
        string candidatePath)
    {
        var score = ScoreFileSearchCandidate(currentChunkFilePath, request.Query, request.PathScope, candidatePath);
        if (candidatePath.EndsWith(requestedPath, StringComparison.OrdinalIgnoreCase) ||
            requestedPath.EndsWith(candidatePath, StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        var requestedFileName = Path.GetFileName(requestedPath);
        if (string.Equals(Path.GetFileName(candidatePath), requestedFileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score;
    }

    private static (int StartLine, int EndLine, string Content) ExtractFileExcerpt(string content, int startLine, int maxLines)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0)
        {
            return (1, 1, string.Empty);
        }

        var safeStart = Math.Clamp(startLine, 1, lines.Length);
        var startIndex = safeStart - 1;
        var endExclusive = Math.Min(lines.Length, startIndex + maxLines);
        var excerptLines = lines[startIndex..endExclusive];
        var excerpt = string.Join('\n', excerptLines);
        return (safeStart, endExclusive, TrimToolOutput(excerpt));
    }

    private static string NormalizePathScope(string pathScope)
    {
        return string.IsNullOrWhiteSpace(pathScope)
            ? string.Empty
            : pathScope.Trim().TrimStart('/').Replace('\\', '/');
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return value
            .Replace('\\', '/')
            .Replace(".", "/", StringComparison.Ordinal)
            .Replace("-", "/", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(SplitIdentifierTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SplitIdentifierTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var current = new List<char>(value.Length);
        foreach (var character in value)
        {
            if (current.Count > 0 &&
                char.IsUpper(character) &&
                (char.IsLower(current[^1]) || (current.Count > 1 && char.IsUpper(current[^1]) && char.IsLower(character))))
            {
                yield return new string(current.ToArray());
                current.Clear();
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Add(char.ToLowerInvariant(character));
            }
            else if (current.Count > 0)
            {
                yield return new string(current.ToArray());
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return new string(current.ToArray());
        }
    }

    private static string TrimToolOutput(string content)
    {
        return content.Length <= MaxToolOutputLength
            ? content
            : content[..MaxToolOutputLength];
    }
}
