using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.Qdrant;

public sealed class QdrantReviewSemanticIndex(
    IHttpClientFactory httpClientFactory,
    IOptions<QdrantOptions> options,
    ShellGitCommandRunner gitCommandRunner,
    ILogger<QdrantReviewSemanticIndex> logger)
    : IReviewSemanticIndex, IReviewCodeSemanticContextService
{
    private const int EmbeddingBatchSize = 6;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public async Task IndexPreparedChunksAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        if (!IsEnabled() || run.Artifacts.PreparedChunks.Count == 0)
        {
            return;
        }

        try
        {
            var chunks = run.Artifacts.PreparedChunks
                .Select(chunk => chunk?.Trim())
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
                .Cast<string>()
                .ToArray();
            if (chunks.Length == 0)
            {
                return;
            }

            var vectors = await BuildEmbeddingsAsync(chunks, cancellationToken);
            if (vectors.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync(vectors[0].Length, cancellationToken);

            var targetKey = BuildTargetKey(run.Target);
            var points = chunks
                .Select((chunk, index) => new QdrantPoint(
                    Id: CreatePointId($"{run.Id:N}:chunk:{index}"),
                    Vector: vectors[Math.Min(index, vectors.Count - 1)],
                    Payload: new Dictionary<string, object?>
                    {
                        ["entity_type"] = "prepared_chunk",
                        ["run_id"] = run.Id.ToString("N"),
                        ["target_key"] = targetKey,
                        ["target_kind"] = run.Target.Kind.ToString(),
                        ["pull_request_url"] = run.Target.PullRequestUrl,
                        ["repository_name"] = run.Target.RepositoryName,
                        ["source_branch"] = run.Target.SourceBranch,
                        ["target_branch"] = run.Target.TargetBranch,
                        ["chunk_index"] = index,
                        ["chunk_text"] = chunk,
                        ["file_path"] = ExtractChunkFilePath(chunk),
                        ["created_at"] = run.CreatedAt
                    }))
                .ToArray();

            await UpsertPointsAsync(points, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant chunk indexing failed for review run {RunId}", run.Id);
        }
    }

    public async Task IndexFindingsAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        if (!IsEnabled() || run.Findings.Count == 0)
        {
            return;
        }

        try
        {
            var findingTexts = run.Findings
                .Select(BuildFindingText)
                .ToArray();
            var vectors = await BuildEmbeddingsAsync(findingTexts, cancellationToken);
            if (vectors.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync(vectors[0].Length, cancellationToken);

            var targetKey = BuildTargetKey(run.Target);
            var points = run.Findings
                .Select((finding, index) => new QdrantPoint(
                    Id: CreatePointId($"{run.Id:N}:finding:{index}"),
                    Vector: vectors[Math.Min(index, vectors.Count - 1)],
                    Payload: new Dictionary<string, object?>
                    {
                        ["entity_type"] = "finding",
                        ["run_id"] = run.Id.ToString("N"),
                        ["target_key"] = targetKey,
                        ["target_kind"] = run.Target.Kind.ToString(),
                        ["pull_request_url"] = run.Target.PullRequestUrl,
                        ["repository_name"] = run.Target.RepositoryName,
                        ["source_branch"] = run.Target.SourceBranch,
                        ["target_branch"] = run.Target.TargetBranch,
                        ["file"] = finding.File,
                        ["line_hint"] = finding.LineHint,
                        ["start_line"] = finding.StartLine,
                        ["end_line"] = finding.EndLine,
                        ["severity"] = finding.Severity.ToString(),
                        ["category"] = finding.Category.ToString(),
                        ["source"] = finding.Source.ToString(),
                        ["title"] = finding.Title,
                        ["description"] = finding.Description,
                        ["existing_code"] = finding.ExistingCode,
                        ["suggestion"] = finding.Suggestion,
                        ["created_at"] = run.CreatedAt
                    }))
                .ToArray();

            await UpsertPointsAsync(points, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant finding indexing failed for review run {RunId}", run.Id);
        }
    }

    public async Task<IReadOnlyList<string>> SearchPreparedChunksAsync(
        ReviewRun run,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        try
        {
            var vector = await BuildEmbeddingAsync(query, cancellationToken);
            if (vector.Length == 0)
            {
                return [];
            }

            await EnsureInitializedAsync(vector.Length, cancellationToken);

            var response = await SearchAsync(
                vector,
                Math.Max(1, limit),
                new QdrantFilter(
                    [
                        new QdrantFieldCondition("entity_type", new QdrantMatchValue("prepared_chunk")),
                        new QdrantFieldCondition("run_id", new QdrantMatchValue(run.Id.ToString("N")))
                    ]),
                cancellationToken);

            return response?.Result?
                .Select(item => item.Payload?.ChunkText?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .Cast<string>()
                .ToArray()
                ?? [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant chunk retrieval failed for review run {RunId}", run.Id);
            return [];
        }
    }

    public async Task<IReadOnlyList<SemanticFindingMatch>> SearchHistoricalFindingsAsync(
        ReviewRun run,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        try
        {
            var vector = await BuildEmbeddingAsync(query, cancellationToken);
            if (vector.Length == 0)
            {
                return [];
            }

            await EnsureInitializedAsync(vector.Length, cancellationToken);

            var response = await SearchAsync(
                vector,
                Math.Max(limit * 3, limit + 2),
                new QdrantFilter(
                    [
                        new QdrantFieldCondition("entity_type", new QdrantMatchValue("finding")),
                        new QdrantFieldCondition("target_key", new QdrantMatchValue(BuildTargetKey(run.Target)))
                    ]),
                cancellationToken);

            var referenceTime = run.CreatedAt == default ? DateTimeOffset.UtcNow : run.CreatedAt;
            var halfLifeDays = Math.Max(0d, options.Value.HistoricalFindingHalfLifeDays);

            return response?.Result?
                .Select(item => new HistoricalFindingCandidate(
                    item.Payload,
                    BuildRecencyWeightedScore(item.Score, item.Payload?.CreatedAt, referenceTime, halfLifeDays)))
                .Where(candidate =>
                    candidate.Payload is not null &&
                    !string.Equals(candidate.Payload.RunId, run.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.WeightedScore)
                .ThenByDescending(candidate => candidate.Payload?.CreatedAt ?? DateTimeOffset.MinValue)
                .Select(candidate => candidate.Payload!)
                .Select(payload => new SemanticFindingMatch(
                    ParseGuidOrDefault(payload.RunId),
                    payload.CreatedAt ?? DateTimeOffset.MinValue,
                    payload.File ?? string.Empty,
                    payload.StartLine,
                    payload.EndLine,
                    Enum.TryParse<FindingSeverity>(payload.Severity, true, out var severity) ? severity : FindingSeverity.Medium,
                    Enum.TryParse<FindingCategory>(payload.Category, true, out var category) ? category : FindingCategory.Bug,
                    Enum.TryParse<ReviewFindingSource>(payload.Source, true, out var source) ? source : ReviewFindingSource.InitialReview,
                    payload.Title ?? string.Empty,
                    payload.Description ?? string.Empty,
                    payload.ExistingCode ?? string.Empty,
                    payload.Suggestion ?? string.Empty))
                .Where(match => !string.IsNullOrWhiteSpace(match.File) && !string.IsNullOrWhiteSpace(match.Title))
                .DistinctBy(match => $"{match.RunId:N}|{match.File}|{match.StartLine}|{match.Title}")
                .Take(limit)
                .ToArray()
                ?? [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant history retrieval failed for review run {RunId}", run.Id);
            return [];
        }
    }

    public async Task<IReadOnlyList<ReviewWorkspaceToolResponse>> BuildContextAsync(
        Guid runId,
        DiffAcquisitionResult diffResult,
        PreprocessedDiff preprocessed,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled() ||
            !options.Value.CodeContextEnabled ||
            string.IsNullOrWhiteSpace(diffResult.RepositoryPath) ||
            string.IsNullOrWhiteSpace(diffResult.SourceRef))
        {
            return [];
        }

        try
        {
            var repositoryName = string.IsNullOrWhiteSpace(diffResult.RepositoryName)
                ? Path.GetFileName(diffResult.RepositoryPath)
                : diffResult.RepositoryName;
            var sourceCommit = await ResolveCommitShaAsync(
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceCommit))
            {
                return [];
            }

            var sourceSnapshotKey = BuildCodeSnapshotKey(repositoryName, sourceCommit, "source");
            var sourceFiles = await SelectSourceCodeContextFilesAsync(
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                preprocessed.ChangedFiles,
                cancellationToken);
            await IndexCodeSnapshotAsync(
                repositoryName,
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                sourceCommit,
                "source",
                sourceSnapshotKey,
                sourceFiles,
                cancellationToken);

            string? targetCommit = null;
            string? targetSnapshotKey = null;
            if (!string.IsNullOrWhiteSpace(diffResult.TargetRef))
            {
                targetCommit = await ResolveCommitShaAsync(
                    diffResult.RepositoryPath,
                    diffResult.TargetRef,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(targetCommit))
                {
                    targetSnapshotKey = BuildCodeSnapshotKey(repositoryName, targetCommit, "target");
                    var targetFiles = SelectTargetCodeContextFiles(preprocessed.ChangedFiles);
                    await IndexCodeSnapshotAsync(
                        repositoryName,
                        diffResult.RepositoryPath,
                        diffResult.TargetRef,
                        targetCommit,
                        "target",
                        targetSnapshotKey,
                        targetFiles,
                        cancellationToken);
                }
            }

            var queries = BuildCodeContextQueries(
                preprocessed,
                Math.Max(1, options.Value.CodeContextMaxQueries));
            if (queries.Count == 0)
            {
                return [];
            }

            var candidates = new List<CodeContextSnippetCandidate>();
            foreach (var query in queries)
            {
                candidates.AddRange(await SearchCodeSnapshotAsync(
                    sourceSnapshotKey,
                    "source",
                    sourceCommit,
                    query,
                    Math.Max(1, options.Value.CodeContextTopKPerQuery),
                    cancellationToken));

                if (!string.IsNullOrWhiteSpace(targetSnapshotKey) &&
                    !string.IsNullOrWhiteSpace(targetCommit))
                {
                    candidates.AddRange(await SearchCodeSnapshotAsync(
                        targetSnapshotKey,
                        "target",
                        targetCommit,
                        query,
                        Math.Max(1, options.Value.CodeContextTopKPerQuery),
                        cancellationToken));
                }
            }

            var maxSnippets = Math.Max(1, options.Value.CodeContextMaxSnippets);
            var responses = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Content))
                .GroupBy(candidate => $"{candidate.RevisionKind}|{candidate.FilePath}|{candidate.StartLine}")
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.RevisionKind, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(maxSnippets)
                .Select(candidate => new ReviewWorkspaceToolResponse(
                    "semantic_code_search",
                    $"{candidate.RevisionKind}@{ShortSha(candidate.CommitSha)} score={candidate.Score:F3}",
                    BuildCodeContextSnippetContent(candidate),
                    candidate.FilePath,
                    candidate.StartLine,
                    candidate.EndLine))
                .ToArray();

            logger.LogInformation(
                "Semantic code context for run {RunId}: sourceFiles={SourceFiles}, queries={Queries}, snippets={Snippets}",
                runId,
                sourceFiles.Count,
                queries.Count,
                responses.Length);

            return responses;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Semantic code context retrieval failed for run {RunId}", runId);
            return [];
        }
    }

    public async Task DeleteTargetAsync(ReviewTargetDescriptor target, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return;
        }

        try
        {
            var vectorSize = Math.Max(32, options.Value.VectorSize);
            await EnsureInitializedAsync(vectorSize, cancellationToken);

            var client = CreateQdrantClient();
            using var response = await client.PostAsJsonAsync(
                $"/collections/{options.Value.CollectionName}/points/delete?wait=true",
                new QdrantDeleteRequest(
                    new QdrantFilter(
                        [
                            new QdrantFieldCondition("target_key", new QdrantMatchValue(BuildTargetKey(target)))
                        ])),
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant target cleanup failed for {TargetTitle}", target.Title);
        }
    }

    private async Task<IReadOnlyList<string>> SelectSourceCodeContextFilesAsync(
        string repositoryPath,
        string revision,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var changedSet = changedFiles
            .Where(IsEligibleCodeContextFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = await ListGitFilesAsync(repositoryPath, revision, cancellationToken);
        var maxFiles = Math.Max(1, options.Value.CodeContextMaxSourceFiles);

        return files
            .Where(IsEligibleCodeContextFile)
            .OrderBy(file => changedSet.Contains(file) ? 0 : 1)
            .ThenBy(file => file.Count(character => character == '/'))
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToArray();
    }

    private IReadOnlyList<string> SelectTargetCodeContextFiles(IReadOnlyList<string> changedFiles)
    {
        var maxFiles = Math.Max(1, options.Value.CodeContextMaxTargetFiles);
        return changedFiles
            .Where(IsEligibleCodeContextFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToArray();
    }

    private async Task IndexCodeSnapshotAsync(
        string repositoryName,
        string repositoryPath,
        string revision,
        string commitSha,
        string revisionKind,
        string snapshotKey,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        var chunks = new List<CodeContextChunk>();
        foreach (var file in files)
        {
            var content = await TryReadGitFileAsync(repositoryPath, revision, file, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            chunks.AddRange(BuildCodeContextChunks(file, content));
        }

        if (chunks.Count == 0)
        {
            return;
        }

        var texts = chunks
            .Select(chunk => $"{chunk.FilePath}\n{chunk.Content}")
            .ToArray();
        var vectors = await BuildEmbeddingsAsync(texts, cancellationToken);
        if (vectors.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(vectors[0].Length, cancellationToken);

        var points = chunks
            .Select((chunk, index) => new QdrantPoint(
                Id: CreatePointId($"code:{snapshotKey}:{chunk.FilePath}:{chunk.StartLine}:{chunk.EndLine}"),
                Vector: vectors[Math.Min(index, vectors.Count - 1)],
                Payload: new Dictionary<string, object?>
                {
                    ["entity_type"] = "code_context",
                    ["repository_name"] = repositoryName,
                    ["snapshot_key"] = snapshotKey,
                    ["revision_kind"] = revisionKind,
                    ["commit_sha"] = commitSha,
                    ["file_path"] = chunk.FilePath,
                    ["start_line"] = chunk.StartLine,
                    ["end_line"] = chunk.EndLine,
                    ["chunk_text"] = chunk.Content,
                    ["indexed_at"] = DateTimeOffset.UtcNow
                }))
            .ToArray();

        await UpsertPointsAsync(points, cancellationToken);
    }

    private async Task<IReadOnlyList<CodeContextSnippetCandidate>> SearchCodeSnapshotAsync(
        string snapshotKey,
        string revisionKind,
        string commitSha,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var vector = await BuildEmbeddingAsync(query, cancellationToken);
        if (vector.Length == 0)
        {
            return [];
        }

        await EnsureInitializedAsync(vector.Length, cancellationToken);

        var response = await SearchAsync(
            vector,
            Math.Max(1, limit),
            new QdrantFilter(
                [
                    new QdrantFieldCondition("entity_type", new QdrantMatchValue("code_context")),
                    new QdrantFieldCondition("snapshot_key", new QdrantMatchValue(snapshotKey))
                ]),
            cancellationToken);

        return response?.Result?
            .Select(item => new CodeContextSnippetCandidate(
                revisionKind,
                commitSha,
                query,
                item.Score,
                item.Payload?.FilePath ?? string.Empty,
                item.Payload?.StartLine ?? 0,
                item.Payload?.EndLine ?? 0,
                item.Payload?.ChunkText ?? string.Empty))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.FilePath) &&
                                !string.IsNullOrWhiteSpace(candidate.Content))
            .ToArray()
            ?? [];
    }

    private async Task<IReadOnlyList<string>> ListGitFilesAsync(
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
            .Where(path => !IsExcludedCodeContextPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string> ResolveCommitShaAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await gitCommandRunner.RunAsync(
                    repositoryPath,
                    ["rev-parse", revision],
                    cancellationToken))
                .Trim();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve commit sha for {Revision}", revision);
            return string.Empty;
        }
    }

    private async Task<string> TryReadGitFileAsync(
        string repositoryPath,
        string revision,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var sizeText = await gitCommandRunner.RunAsync(
                repositoryPath,
                ["cat-file", "-s", $"{revision}:{filePath}"],
                cancellationToken);
            if (long.TryParse(sizeText.Trim(), out var size) &&
                size > Math.Max(1, options.Value.CodeContextMaxFileBytes))
            {
                return string.Empty;
            }

            return await gitCommandRunner.RunAsync(
                repositoryPath,
                ["show", $"{revision}:{filePath}"],
                cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private IReadOnlyList<CodeContextChunk> BuildCodeContextChunks(string filePath, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            return [];
        }

        var maxLines = Math.Clamp(options.Value.CodeContextChunkMaxLines, 40, 240);
        var overlap = Math.Clamp(options.Value.CodeContextChunkOverlapLines, 0, maxLines / 2);
        var step = Math.Max(1, maxLines - overlap);
        var chunks = new List<CodeContextChunk>();

        for (var start = 0; start < lines.Length; start += step)
        {
            var selected = lines
                .Skip(start)
                .Take(maxLines)
                .ToArray();
            var chunkText = string.Join('\n', selected).Trim();
            if (chunkText.Length < 80)
            {
                continue;
            }

            chunks.Add(new CodeContextChunk(
                filePath,
                start + 1,
                start + selected.Length,
                chunkText));

            if (start + maxLines >= lines.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private static IReadOnlyList<string> BuildCodeContextQueries(PreprocessedDiff preprocessed, int maxQueries)
    {
        var queries = new List<string>();

        queries.AddRange(preprocessed.ReviewHints.Select(hint => string.Join(
            " ",
            [
                hint.RuleId,
                hint.Category,
                hint.FilePath,
                hint.Message,
                hint.Evidence,
                hint.SuggestedVerification
            ])));

        queries.AddRange(preprocessed.ChangedFiles
            .Where(IsEligibleCodeContextFile)
            .Select(file => $"changed file dependencies implementation contract {file} {Path.GetFileNameWithoutExtension(file)}"));

        return queries
            .Select(NormalizeWhitespace)
            .Where(query => query.Length >= 12)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxQueries)
            .ToArray();
    }

    private static string BuildCodeContextSnippetContent(CodeContextSnippetCandidate candidate)
    {
        return string.Join(
            "\n",
            [
                $"Query: {TrimForLog(candidate.Query, 240)}",
                $"Revision: {candidate.RevisionKind}@{ShortSha(candidate.CommitSha)}",
                $"Score: {candidate.Score:F3}",
                candidate.Content
            ]);
    }

    private static string BuildCodeSnapshotKey(string repositoryName, string commitSha, string revisionKind)
        => $"code:{repositoryName.Trim().ToLowerInvariant()}:{commitSha}:{revisionKind}";

    private static string ShortSha(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value[..Math.Min(8, value.Length)];

    private static bool IsEligibleCodeContextFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsExcludedCodeContextPath(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedCodeContextPath(string path)
    {
        return path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);

    private async Task UpsertPointsAsync(IReadOnlyList<QdrantPoint> points, CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            return;
        }

        var client = CreateQdrantClient();
        using var response = await client.PutAsJsonAsync(
            $"/collections/{options.Value.CollectionName}/points?wait=true",
            new QdrantUpsertRequest(points),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Qdrant upsert failed with {(int)response.StatusCode} ({response.StatusCode}): {body}");
        }
    }

    private async Task<QdrantSearchResponse?> SearchAsync(
        float[] vector,
        int limit,
        QdrantFilter filter,
        CancellationToken cancellationToken)
    {
        var client = CreateQdrantClient();
        using var response = await client.PostAsJsonAsync(
            $"/collections/{options.Value.CollectionName}/points/search",
            new QdrantSearchRequest(vector, limit, true, filter),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<QdrantSearchResponse>(stream, cancellationToken: cancellationToken);
    }

    private async Task EnsureInitializedAsync(int vectorSize, CancellationToken cancellationToken)
    {
        if (_initialized || !IsEnabled())
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var client = CreateQdrantClient();
            using var response = await client.PutAsJsonAsync(
                $"/collections/{options.Value.CollectionName}",
                new QdrantCreateCollectionRequest(
                    new QdrantVectorsConfiguration(vectorSize, "Cosine")),
                cancellationToken);

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                response.EnsureSuccessStatusCode();
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private HttpClient CreateQdrantClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Qdrant);
        client.BaseAddress = new Uri(options.Value.Url.TrimEnd('/'));
        client.DefaultRequestHeaders.Remove("api-key");
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            client.DefaultRequestHeaders.Add("api-key", options.Value.ApiKey);
        }

        return client;
    }

    private HttpClient CreateEmbeddingClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.OllamaLlm);
        var baseUrl = ResolveEmbeddingBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Qdrant embeddings require Qdrant:EmbeddingBaseUrl or LOCAL_OLLAMA_BASE_URL.");
        }

        client.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
        return client;
    }

    private async Task<IReadOnlyList<float[]>> BuildEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        if (!CanUseOllamaEmbeddings())
        {
            return texts.Select(text => BuildFallbackEmbedding(text, options.Value.VectorSize)).ToArray();
        }

        try
        {
            var client = CreateEmbeddingClient();
            var embeddings = new List<float[]>(texts.Count);
            for (var offset = 0; offset < texts.Count; offset += EmbeddingBatchSize)
            {
                var batch = texts.Skip(offset).Take(EmbeddingBatchSize).ToArray();
                using var response = await client.PostAsJsonAsync(
                    "/api/embed",
                    new OllamaEmbedRequest(options.Value.EmbeddingModel, batch),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    logger.LogWarning(
                        "Ollama embedding batch request failed with {StatusCode}: {Body}; falling back to deterministic embeddings",
                        response.StatusCode,
                        TrimForLog(body, 400));
                    return texts.Select(text => BuildFallbackEmbedding(text, options.Value.VectorSize)).ToArray();
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var parsed = await JsonSerializer.DeserializeAsync<OllamaEmbedResponse>(stream, cancellationToken: cancellationToken);
                var batchEmbeddings = parsed?.Embeddings?
                    .Where(embedding => embedding is { Length: > 0 })
                    .ToArray();

                if (batchEmbeddings is not { Length: > 0 })
                {
                    return texts.Select(text => BuildFallbackEmbedding(text, options.Value.VectorSize)).ToArray();
                }

                embeddings.AddRange(batchEmbeddings);
            }

            return embeddings.Count > 0
                ? embeddings
                : texts.Select(text => BuildFallbackEmbedding(text, options.Value.VectorSize)).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Ollama embedding request failed; falling back to deterministic embeddings");
            return texts.Select(text => BuildFallbackEmbedding(text, options.Value.VectorSize)).ToArray();
        }
    }

    private async Task<float[]> BuildEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var embeddings = await BuildEmbeddingsAsync([text], cancellationToken);
        return embeddings.FirstOrDefault() ?? [];
    }

    private bool CanUseOllamaEmbeddings()
    {
        return IsEnabled() &&
               !string.IsNullOrWhiteSpace(ResolveEmbeddingBaseUrl()) &&
               !string.IsNullOrWhiteSpace(options.Value.EmbeddingModel);
    }

    private string ResolveEmbeddingBaseUrl()
    {
        return !string.IsNullOrWhiteSpace(options.Value.EmbeddingBaseUrl)
            ? options.Value.EmbeddingBaseUrl
            : Environment.GetEnvironmentVariable("LOCAL_OLLAMA_BASE_URL") ?? string.Empty;
    }

    private bool IsEnabled()
    {
        return options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Url);
    }

    private static float[] BuildFallbackEmbedding(string text, int vectorSize)
    {
        var vector = new float[Math.Max(32, vectorSize)];
        foreach (var token in Tokenize(text))
        {
            var tokenBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var firstIndex = (int)(BitConverter.ToUInt32(tokenBytes, 0) % (uint)vector.Length);
            var secondIndex = (int)(BitConverter.ToUInt32(tokenBytes, 8) % (uint)vector.Length);
            var weight = 1f + Math.Min(token.Length, 24) / 24f;
            vector[firstIndex] += (tokenBytes[4] & 1) == 0 ? weight : -weight;
            vector[secondIndex] += (tokenBytes[12] & 1) == 0 ? weight * 0.5f : -weight * 0.5f;
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm <= 0f)
        {
            return vector;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= norm;
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return Regex.Split(text.ToLowerInvariant(), @"[^a-zа-я0-9_]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal);
    }

    private static string BuildFindingText(ReviewFinding finding)
    {
        return string.Join(
            "\n",
            [
                finding.File,
                finding.Title,
                finding.Description,
                finding.ExistingCode,
                finding.Suggestion
            ]);
    }

    private static string BuildTargetKey(ReviewTargetDescriptor target)
    {
        return target.Kind switch
        {
            ReviewTargetKind.PullRequest => $"pr:{target.PullRequestUrl?.Trim().ToLowerInvariant()}",
            ReviewTargetKind.BranchComparison => $"branch:{target.RepositoryName?.Trim().ToLowerInvariant()}|{target.SourceBranch?.Trim().ToLowerInvariant()}|{target.TargetBranch?.Trim().ToLowerInvariant()}",
            _ => target.Title.Trim().ToLowerInvariant()
        };
    }

    private static string ExtractChunkFilePath(string chunk)
    {
        var firstLine = chunk.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return string.Empty;
        }

        var match = Regex.Match(firstLine, "^## File: '(.+)'$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static Guid ParseGuidOrDefault(string? value)
    {
        return Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
    }

    private static double BuildRecencyWeightedScore(
        double semanticScore,
        DateTimeOffset? createdAt,
        DateTimeOffset referenceTime,
        double halfLifeDays)
    {
        var safeScore = semanticScore > 0d ? semanticScore : 1d;
        if (createdAt is null || halfLifeDays <= 0d)
        {
            return safeScore;
        }

        var ageDays = Math.Max(0d, (referenceTime - createdAt.Value).TotalDays);
        var decay = Math.Pow(0.5d, ageDays / Math.Max(1d, halfLifeDays));
        return safeScore * decay;
    }

    private static string CreatePointId(string rawValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes).ToString("D");
    }

    private static string TrimForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private sealed record QdrantVectorsConfiguration(
        [property: JsonPropertyName("size")] int Size,
        [property: JsonPropertyName("distance")] string Distance);

    private sealed record QdrantCreateCollectionRequest(
        [property: JsonPropertyName("vectors")] QdrantVectorsConfiguration Vectors);

    private sealed record QdrantPoint(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("vector")] float[] Vector,
        [property: JsonPropertyName("payload")] IReadOnlyDictionary<string, object?> Payload);

    private sealed record QdrantUpsertRequest(
        [property: JsonPropertyName("points")] IReadOnlyList<QdrantPoint> Points);

    private sealed record QdrantSearchRequest(
        [property: JsonPropertyName("vector")] float[] Vector,
        [property: JsonPropertyName("limit")] int Limit,
        [property: JsonPropertyName("with_payload")] bool WithPayload,
        [property: JsonPropertyName("filter")] QdrantFilter Filter);

    private sealed record QdrantDeleteRequest(
        [property: JsonPropertyName("filter")] QdrantFilter Filter);

    private sealed record QdrantFilter(
        [property: JsonPropertyName("must")] IReadOnlyList<QdrantFieldCondition> Must);

    private sealed record QdrantFieldCondition(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("match")] QdrantMatchValue Match);

    private sealed record QdrantMatchValue(
        [property: JsonPropertyName("value")] string Value);

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public float[][]? Embeddings { get; init; }
    }

    private sealed class QdrantSearchResponse
    {
        [JsonPropertyName("result")]
        public IReadOnlyList<QdrantSearchResultItem>? Result { get; init; }
    }

    private sealed class QdrantSearchResultItem
    {
        [JsonPropertyName("score")]
        public double Score { get; init; }

        [JsonPropertyName("payload")]
        public QdrantPayload? Payload { get; init; }
    }

    private sealed record HistoricalFindingCandidate(
        QdrantPayload? Payload,
        double WeightedScore);

    private sealed record CodeContextChunk(
        string FilePath,
        int StartLine,
        int EndLine,
        string Content);

    private sealed record CodeContextSnippetCandidate(
        string RevisionKind,
        string CommitSha,
        string Query,
        double Score,
        string FilePath,
        int StartLine,
        int EndLine,
        string Content);

    private sealed class QdrantPayload
    {
        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }

        [JsonPropertyName("chunk_text")]
        public string? ChunkText { get; init; }

        [JsonPropertyName("file_path")]
        public string? FilePath { get; init; }

        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("start_line")]
        public int StartLine { get; init; }

        [JsonPropertyName("end_line")]
        public int EndLine { get; init; }

        [JsonPropertyName("severity")]
        public string? Severity { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("existing_code")]
        public string? ExistingCode { get; init; }

        [JsonPropertyName("suggestion")]
        public string? Suggestion { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }
    }
}
