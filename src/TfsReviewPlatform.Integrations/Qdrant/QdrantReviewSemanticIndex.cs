using System.Diagnostics;
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
    private readonly SemaphoreSlim _codeContextCleanupLock = new(1, 1);
    private DateTimeOffset _lastCodeContextCleanup = DateTimeOffset.MinValue;
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

    public async Task<ReviewCodeSemanticContextResult> BuildContextAsync(
        Guid runId,
        DiffAcquisitionResult diffResult,
        PreprocessedDiff preprocessed,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (!IsEnabled() || !options.Value.CodeContextEnabled)
        {
            return BuildSkippedCodeContextResult(
                enabled: false,
                status: "disabled",
                message: "Semantic code context is disabled.",
                elapsedMilliseconds: GetElapsedMilliseconds(startedAt));
        }

        if (string.IsNullOrWhiteSpace(diffResult.RepositoryPath) ||
            string.IsNullOrWhiteSpace(diffResult.SourceRef))
        {
            return BuildSkippedCodeContextResult(
                enabled: true,
                status: "skipped_missing_repository_context",
                message: "Repository path or source ref is missing.",
                elapsedMilliseconds: GetElapsedMilliseconds(startedAt));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = options.Value.CodeContextTimeoutSeconds;
        if (timeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        var workToken = timeoutCts.Token;
        try
        {
            await CleanupExpiredCodeContextAsync(workToken);

            var repositoryName = string.IsNullOrWhiteSpace(diffResult.RepositoryName)
                ? Path.GetFileName(diffResult.RepositoryPath)
                : diffResult.RepositoryName;
            var sourceCommit = await ResolveCommitShaAsync(
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                workToken);
            if (string.IsNullOrWhiteSpace(sourceCommit))
            {
                return BuildSkippedCodeContextResult(
                    enabled: true,
                    status: "skipped_source_commit_unresolved",
                    message: "Source commit could not be resolved.",
                    elapsedMilliseconds: GetElapsedMilliseconds(startedAt));
            }

            var sourceSnapshotKey = BuildCodeSnapshotKey(repositoryName, sourceCommit, "source");
            var sourceFiles = await SelectSourceCodeContextFilesAsync(
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                preprocessed.ChangedFiles,
                workToken);
            var sourceIndex = await IndexCodeSnapshotAsync(
                repositoryName,
                diffResult.RepositoryPath,
                diffResult.SourceRef,
                sourceCommit,
                "source",
                sourceSnapshotKey,
                sourceFiles,
                workToken);

            string? targetCommit = null;
            string? targetSnapshotKey = null;
            IReadOnlyList<string> targetFiles = [];
            var targetIndex = CodeSnapshotIndexResult.Empty;
            if (!string.IsNullOrWhiteSpace(diffResult.TargetRef))
            {
                targetCommit = await ResolveCommitShaAsync(
                    diffResult.RepositoryPath,
                    diffResult.TargetRef,
                    workToken);
                if (!string.IsNullOrWhiteSpace(targetCommit))
                {
                    targetSnapshotKey = BuildCodeSnapshotKey(repositoryName, targetCommit, "target");
                    targetFiles = SelectTargetCodeContextFiles(preprocessed.ChangedFiles);
                    targetIndex = await IndexCodeSnapshotAsync(
                        repositoryName,
                        diffResult.RepositoryPath,
                        diffResult.TargetRef,
                        targetCommit,
                        "target",
                        targetSnapshotKey,
                        targetFiles,
                        workToken);
                }
            }

            var queries = BuildCodeContextQueries(
                preprocessed,
                Math.Max(1, options.Value.CodeContextMaxQueries));
            if (queries.Count == 0)
            {
                return ReviewCodeSemanticContextResult.Empty(BuildCodeContextDiagnostics(
                    enabled: true,
                    attempted: true,
                    succeeded: false,
                    timedOut: false,
                    status: "skipped_no_queries",
                    message: "No semantic search queries were produced from the diff.",
                    elapsedMilliseconds: GetElapsedMilliseconds(startedAt),
                    sourceCommitSha: sourceCommit,
                    targetCommitSha: targetCommit,
                    sourceFilesSelected: sourceFiles.Count,
                    targetFilesSelected: targetFiles.Count,
                    sourceIndex: sourceIndex,
                    targetIndex: targetIndex,
                    queryCount: 0,
                    candidateCount: 0,
                    snippets: []));
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
                    workToken));

                if (!string.IsNullOrWhiteSpace(targetSnapshotKey) &&
                    !string.IsNullOrWhiteSpace(targetCommit))
                {
                    candidates.AddRange(await SearchCodeSnapshotAsync(
                        targetSnapshotKey,
                        "target",
                        targetCommit,
                        query,
                        Math.Max(1, options.Value.CodeContextTopKPerQuery),
                        workToken));
                }
            }

            var maxSnippets = Math.Max(1, options.Value.CodeContextMaxSnippets);
            var selectedCandidates = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Content))
                .GroupBy(candidate => $"{candidate.RevisionKind}|{candidate.FilePath}|{candidate.StartLine}")
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.RevisionKind, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(maxSnippets)
                .ToArray();
            var responses = selectedCandidates
                .Select(candidate => new ReviewWorkspaceToolResponse(
                    "semantic_code_search",
                    $"{candidate.RevisionKind}@{ShortSha(candidate.CommitSha)} score={candidate.Score:F3}",
                    BuildCodeContextSnippetContent(candidate),
                    candidate.FilePath,
                    candidate.StartLine,
                    candidate.EndLine))
                .ToArray();
            var snippetArtifacts = selectedCandidates
                .Select(BuildCodeContextSnippetArtifact)
                .ToArray();

            logger.LogInformation(
                "Semantic code context for run {RunId}: sourceFiles={SourceFiles}, targetFiles={TargetFiles}, sourceCacheHit={SourceCacheHit}, targetCacheHit={TargetCacheHit}, sourceIndexed={SourceIndexedFiles}/{SourceChunks}, targetIndexed={TargetIndexedFiles}/{TargetChunks}, targetMissing={TargetMissing}, targetTooLarge={TargetTooLarge}, targetEmpty={TargetEmpty}, targetWithoutChunks={TargetWithoutChunks}, targetReadFailed={TargetReadFailed}, queries={Queries}, candidates={Candidates}, snippets={Snippets}, elapsedMs={ElapsedMilliseconds}",
                runId,
                sourceFiles.Count,
                targetFiles.Count,
                sourceIndex.CacheHit,
                targetIndex.CacheHit,
                sourceIndex.FilesIndexed,
                sourceIndex.ChunksIndexed,
                targetIndex.FilesIndexed,
                targetIndex.ChunksIndexed,
                targetIndex.FilesMissing,
                targetIndex.FilesTooLarge,
                targetIndex.FilesEmpty,
                targetIndex.FilesWithoutChunks,
                targetIndex.FilesReadFailed,
                queries.Count,
                candidates.Count,
                responses.Length,
                GetElapsedMilliseconds(startedAt));

            return new ReviewCodeSemanticContextResult(
                responses,
                BuildCodeContextDiagnostics(
                    enabled: true,
                    attempted: true,
                    succeeded: responses.Length > 0,
                    timedOut: false,
                    status: responses.Length > 0 ? "ready" : "empty",
                    message: BuildCodeContextMessage(
                        responses.Length,
                        targetFiles.Count,
                        targetIndex),
                    elapsedMilliseconds: GetElapsedMilliseconds(startedAt),
                    sourceCommitSha: sourceCommit,
                    targetCommitSha: targetCommit,
                    sourceFilesSelected: sourceFiles.Count,
                    targetFilesSelected: targetFiles.Count,
                    sourceIndex: sourceIndex,
                    targetIndex: targetIndex,
                    queryCount: queries.Count,
                    candidateCount: candidates.Count,
                    snippets: snippetArtifacts));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Semantic code context retrieval timed out for run {RunId} after {TimeoutSeconds}s",
                runId,
                timeoutSeconds);
            return ReviewCodeSemanticContextResult.Empty(BuildCodeContextDiagnostics(
                enabled: true,
                attempted: true,
                succeeded: false,
                timedOut: true,
                status: "timeout",
                message: $"Semantic code context timed out after {timeoutSeconds}s.",
                elapsedMilliseconds: GetElapsedMilliseconds(startedAt),
                sourceCommitSha: null,
                targetCommitSha: null,
                sourceFilesSelected: 0,
                targetFilesSelected: 0,
                sourceIndex: CodeSnapshotIndexResult.Empty,
                targetIndex: CodeSnapshotIndexResult.Empty,
                queryCount: 0,
                candidateCount: 0,
                snippets: []));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Semantic code context retrieval failed for run {RunId}", runId);
            return ReviewCodeSemanticContextResult.Empty(BuildCodeContextDiagnostics(
                enabled: true,
                attempted: true,
                succeeded: false,
                timedOut: false,
                status: "failed",
                message: exception.Message,
                elapsedMilliseconds: GetElapsedMilliseconds(startedAt),
                sourceCommitSha: null,
                targetCommitSha: null,
                sourceFilesSelected: 0,
                targetFilesSelected: 0,
                sourceIndex: CodeSnapshotIndexResult.Empty,
                targetIndex: CodeSnapshotIndexResult.Empty,
                queryCount: 0,
                candidateCount: 0,
                snippets: []));
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

    private async Task<CodeSnapshotIndexResult> IndexCodeSnapshotAsync(
        string repositoryName,
        string repositoryPath,
        string revision,
        string commitSha,
        string revisionKind,
        string snapshotKey,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        if (options.Value.CodeContextReuseExistingSnapshots &&
            await CodeSnapshotManifestExistsAsync(snapshotKey, cancellationToken))
        {
            logger.LogInformation(
                "Semantic code context snapshot cache hit for {RepositoryName} {RevisionKind}@{CommitSha}",
                repositoryName,
                revisionKind,
                ShortSha(commitSha));
            return new CodeSnapshotIndexResult(
                CacheHit: true,
                FilesIndexed: 0,
                ChunksIndexed: 0,
                FilesMissing: 0,
                FilesTooLarge: 0,
                FilesEmpty: 0,
                FilesWithoutChunks: 0,
                FilesReadFailed: 0);
        }

        if (files.Count == 0)
        {
            return CodeSnapshotIndexResult.Empty;
        }

        var chunks = new List<CodeContextChunk>();
        var filesMissing = 0;
        var filesTooLarge = 0;
        var filesEmpty = 0;
        var filesWithoutChunks = 0;
        var filesReadFailed = 0;
        foreach (var file in files)
        {
            var readResult = await TryReadGitFileAsync(repositoryPath, revision, file, cancellationToken);
            switch (readResult.Status)
            {
                case CodeContextFileReadStatus.Missing:
                    filesMissing++;
                    continue;
                case CodeContextFileReadStatus.TooLarge:
                    filesTooLarge++;
                    continue;
                case CodeContextFileReadStatus.Empty:
                    filesEmpty++;
                    continue;
                case CodeContextFileReadStatus.ReadFailed:
                    filesReadFailed++;
                    continue;
            }

            var fileChunks = BuildCodeContextChunks(file, readResult.Content);
            if (fileChunks.Count == 0)
            {
                filesWithoutChunks++;
                continue;
            }

            chunks.AddRange(fileChunks);
        }

        if (chunks.Count == 0)
        {
            return new CodeSnapshotIndexResult(
                CacheHit: false,
                FilesIndexed: 0,
                ChunksIndexed: 0,
                FilesMissing: filesMissing,
                FilesTooLarge: filesTooLarge,
                FilesEmpty: filesEmpty,
                FilesWithoutChunks: filesWithoutChunks,
                FilesReadFailed: filesReadFailed);
        }

        var indexedFileCount = chunks
            .Select(chunk => chunk.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var texts = chunks
            .Select(chunk => $"{chunk.FilePath}\n{chunk.Content}")
            .ToArray();
        var vectors = await BuildEmbeddingsAsync(texts, cancellationToken);
        if (vectors.Count == 0)
        {
            return CodeSnapshotIndexResult.Empty;
        }

        await EnsureInitializedAsync(vectors[0].Length, cancellationToken);

        var indexedAt = DateTimeOffset.UtcNow;
        var indexedAtUnix = indexedAt.ToUnixTimeSeconds();
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
                    ["indexed_at"] = indexedAt,
                    ["indexed_at_unix"] = indexedAtUnix
                }))
            .Append(new QdrantPoint(
                Id: CreatePointId($"code-manifest:{snapshotKey}"),
                Vector: vectors[0],
                Payload: new Dictionary<string, object?>
                {
                    ["entity_type"] = "code_context_snapshot",
                    ["repository_name"] = repositoryName,
                    ["snapshot_key"] = snapshotKey,
                    ["revision_kind"] = revisionKind,
                    ["commit_sha"] = commitSha,
                    ["file_count"] = indexedFileCount,
                    ["chunk_count"] = chunks.Count,
                    ["indexed_at"] = indexedAt,
                    ["indexed_at_unix"] = indexedAtUnix
                }))
            .ToArray();

        await UpsertPointsAsync(points, cancellationToken);
        logger.LogInformation(
            "Semantic code context snapshot indexed for {RepositoryName} {RevisionKind}@{CommitSha}: files={Files}, chunks={Chunks}",
            repositoryName,
            revisionKind,
            ShortSha(commitSha),
            indexedFileCount,
            chunks.Count);

        return new CodeSnapshotIndexResult(
            CacheHit: false,
            FilesIndexed: indexedFileCount,
            ChunksIndexed: chunks.Count,
            FilesMissing: filesMissing,
            FilesTooLarge: filesTooLarge,
            FilesEmpty: filesEmpty,
            FilesWithoutChunks: filesWithoutChunks,
            FilesReadFailed: filesReadFailed);
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

    private async Task<bool> CodeSnapshotManifestExistsAsync(
        string snapshotKey,
        CancellationToken cancellationToken)
    {
        var count = await CountAsync(
            new QdrantFilter(
                [
                    QdrantFieldCondition.MatchValue("entity_type", "code_context_snapshot"),
                    QdrantFieldCondition.MatchValue("snapshot_key", snapshotKey)
                ]),
            cancellationToken);

        return count > 0;
    }

    private async Task CleanupExpiredCodeContextAsync(CancellationToken cancellationToken)
    {
        var ttlHours = options.Value.CodeContextTtlHours;
        if (ttlHours <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.CodeContextCleanupIntervalMinutes));
        if (now - _lastCodeContextCleanup < interval)
        {
            return;
        }

        await _codeContextCleanupLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now - _lastCodeContextCleanup < interval)
            {
                return;
            }

            await DeleteAsync(
                new QdrantFilter(
                    [
                        QdrantFieldCondition.MatchAnyValue("entity_type", ["code_context", "code_context_snapshot"]),
                        QdrantFieldCondition.RangeLessThan("indexed_at_unix", now.AddHours(-ttlHours).ToUnixTimeSeconds())
                    ]),
                cancellationToken);
            _lastCodeContextCleanup = now;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Qdrant code context cleanup failed");
            _lastCodeContextCleanup = now;
        }
        finally
        {
            _codeContextCleanupLock.Release();
        }
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

    private async Task<CodeContextFileReadResult> TryReadGitFileAsync(
        string repositoryPath,
        string revision,
        string filePath,
        CancellationToken cancellationToken)
    {
        string sizeText;
        try
        {
            sizeText = await gitCommandRunner.RunAsync(
                repositoryPath,
                ["cat-file", "-s", $"{revision}:{filePath}"],
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Semantic code context file is unavailable at {Revision}: {FilePath}",
                revision,
                filePath);
            return CodeContextFileReadResult.Missing;
        }

        if (long.TryParse(sizeText.Trim(), out var size) &&
            size > Math.Max(1, options.Value.CodeContextMaxFileBytes))
        {
            return CodeContextFileReadResult.TooLarge;
        }

        try
        {
            var content = await gitCommandRunner.RunAsync(
                repositoryPath,
                ["show", $"{revision}:{filePath}"],
                cancellationToken);

            return string.IsNullOrWhiteSpace(content)
                ? CodeContextFileReadResult.Empty
                : new CodeContextFileReadResult(CodeContextFileReadStatus.Read, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Semantic code context file failed to read at {Revision}: {FilePath}",
                revision,
                filePath);
            return CodeContextFileReadResult.ReadFailed;
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

    private static string BuildCodeContextMessage(
        int snippetCount,
        int targetFilesSelected,
        CodeSnapshotIndexResult targetIndex)
    {
        var messages = new List<string>
        {
            snippetCount > 0
                ? "Semantic code context was added to the review prompt."
                : "Semantic search completed but returned no snippets."
        };

        if (!targetIndex.CacheHit &&
            targetFilesSelected > 0 &&
            targetIndex.ChunksIndexed == 0)
        {
            messages.Add(BuildSnapshotSkipMessage("Target", targetFilesSelected, targetIndex));
        }

        return string.Join(' ', messages.Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    private static string BuildSnapshotSkipMessage(
        string snapshotName,
        int filesSelected,
        CodeSnapshotIndexResult index)
    {
        if (index.FilesMissing == filesSelected)
        {
            return $"{snapshotName} snapshot produced no chunks because all selected files are absent at that revision; this usually means the PR added new files.";
        }

        var reasons = new List<string>();
        AddReason(reasons, index.FilesMissing, "missing");
        AddReason(reasons, index.FilesTooLarge, "too large");
        AddReason(reasons, index.FilesEmpty, "empty");
        AddReason(reasons, index.FilesWithoutChunks, "without indexable chunks");
        AddReason(reasons, index.FilesReadFailed, "read failed");

        return reasons.Count == 0
            ? $"{snapshotName} snapshot produced no chunks."
            : $"{snapshotName} snapshot produced no chunks: {string.Join(", ", reasons)}.";
    }

    private static void AddReason(List<string> reasons, int count, string label)
    {
        if (count > 0)
        {
            reasons.Add($"{count} {label}");
        }
    }

    private static SemanticCodeContextSnippetArtifact BuildCodeContextSnippetArtifact(
        CodeContextSnippetCandidate candidate)
    {
        return new SemanticCodeContextSnippetArtifact
        {
            RevisionKind = candidate.RevisionKind,
            CommitSha = candidate.CommitSha,
            FilePath = candidate.FilePath,
            StartLine = candidate.StartLine,
            EndLine = candidate.EndLine,
            Score = candidate.Score,
            Query = TrimForLog(candidate.Query, 240)
        };
    }

    private SemanticCodeContextArtifact BuildCodeContextDiagnostics(
        bool enabled,
        bool attempted,
        bool succeeded,
        bool timedOut,
        string status,
        string message,
        long elapsedMilliseconds,
        string? sourceCommitSha,
        string? targetCommitSha,
        int sourceFilesSelected,
        int targetFilesSelected,
        CodeSnapshotIndexResult sourceIndex,
        CodeSnapshotIndexResult targetIndex,
        int queryCount,
        int candidateCount,
        IReadOnlyList<SemanticCodeContextSnippetArtifact> snippets)
    {
        return new SemanticCodeContextArtifact
        {
            Enabled = enabled,
            Attempted = attempted,
            Succeeded = succeeded,
            TimedOut = timedOut,
            CacheReuseEnabled = options.Value.CodeContextReuseExistingSnapshots,
            SourceCacheHit = sourceIndex.CacheHit,
            TargetCacheHit = targetIndex.CacheHit,
            SourceFilesSelected = sourceFilesSelected,
            TargetFilesSelected = targetFilesSelected,
            SourceFilesIndexed = sourceIndex.FilesIndexed,
            TargetFilesIndexed = targetIndex.FilesIndexed,
            SourceChunksIndexed = sourceIndex.ChunksIndexed,
            TargetChunksIndexed = targetIndex.ChunksIndexed,
            SourceFilesMissing = sourceIndex.FilesMissing,
            TargetFilesMissing = targetIndex.FilesMissing,
            SourceFilesTooLarge = sourceIndex.FilesTooLarge,
            TargetFilesTooLarge = targetIndex.FilesTooLarge,
            SourceFilesEmpty = sourceIndex.FilesEmpty,
            TargetFilesEmpty = targetIndex.FilesEmpty,
            SourceFilesWithoutChunks = sourceIndex.FilesWithoutChunks,
            TargetFilesWithoutChunks = targetIndex.FilesWithoutChunks,
            SourceFilesReadFailed = sourceIndex.FilesReadFailed,
            TargetFilesReadFailed = targetIndex.FilesReadFailed,
            QueryCount = queryCount,
            CandidateCount = candidateCount,
            SnippetCount = snippets.Count,
            ElapsedMilliseconds = elapsedMilliseconds,
            Status = status,
            Message = message,
            SourceCommitSha = sourceCommitSha,
            TargetCommitSha = targetCommitSha,
            Snippets = snippets
        };
    }

    private ReviewCodeSemanticContextResult BuildSkippedCodeContextResult(
        bool enabled,
        string status,
        string message,
        long elapsedMilliseconds)
    {
        return ReviewCodeSemanticContextResult.Empty(BuildCodeContextDiagnostics(
            enabled,
            attempted: false,
            succeeded: false,
            timedOut: false,
            status,
            message,
            elapsedMilliseconds,
            sourceCommitSha: null,
            targetCommitSha: null,
            sourceFilesSelected: 0,
            targetFilesSelected: 0,
            sourceIndex: CodeSnapshotIndexResult.Empty,
            targetIndex: CodeSnapshotIndexResult.Empty,
            queryCount: 0,
            candidateCount: 0,
            snippets: []));
    }

    private static string BuildCodeSnapshotKey(string repositoryName, string commitSha, string revisionKind)
        => $"code:{repositoryName.Trim().ToLowerInvariant()}:{commitSha}:{revisionKind}";

    private static string ShortSha(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value[..Math.Min(8, value.Length)];

    private static long GetElapsedMilliseconds(long startedAt)
        => (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

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

    private async Task<long> CountAsync(QdrantFilter filter, CancellationToken cancellationToken)
    {
        var client = CreateQdrantClient();
        using var response = await client.PostAsJsonAsync(
            $"/collections/{options.Value.CollectionName}/points/count",
            new QdrantCountRequest(filter, true),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync<QdrantCountResponse>(stream, cancellationToken: cancellationToken);
        return parsed?.Result?.Count ?? 0;
    }

    private async Task DeleteAsync(QdrantFilter filter, CancellationToken cancellationToken)
    {
        var client = CreateQdrantClient();
        using var response = await client.PostAsJsonAsync(
            $"/collections/{options.Value.CollectionName}/points/delete?wait=true",
            new QdrantDeleteRequest(filter),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
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

    private sealed record QdrantCountRequest(
        [property: JsonPropertyName("filter")] QdrantFilter Filter,
        [property: JsonPropertyName("exact")] bool Exact);

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
        [property: JsonPropertyName("match")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QdrantMatchValue? Match = null,
        [property: JsonPropertyName("range")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QdrantRangeCondition? Range = null)
    {
        public static QdrantFieldCondition MatchValue(string key, string value)
            => new(key, new QdrantMatchValue(value));

        public static QdrantFieldCondition MatchAnyValue(string key, IReadOnlyList<string> values)
            => new(key, new QdrantMatchValue(Any: values));

        public static QdrantFieldCondition RangeLessThan(string key, long value)
            => new(key, Range: new QdrantRangeCondition(Lt: value));
    }

    private sealed record QdrantMatchValue(
        [property: JsonPropertyName("value")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Value = null,
        [property: JsonPropertyName("any")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Any = null);

    private sealed record QdrantRangeCondition(
        [property: JsonPropertyName("lt")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? Lt = null);

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

    private sealed class QdrantCountResponse
    {
        [JsonPropertyName("result")]
        public QdrantCountResult? Result { get; init; }
    }

    private sealed class QdrantCountResult
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
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

    private sealed record CodeSnapshotIndexResult(
        bool CacheHit,
        int FilesIndexed,
        int ChunksIndexed,
        int FilesMissing,
        int FilesTooLarge,
        int FilesEmpty,
        int FilesWithoutChunks,
        int FilesReadFailed)
    {
        public static CodeSnapshotIndexResult Empty { get; } = new(false, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record CodeContextFileReadResult(
        CodeContextFileReadStatus Status,
        string Content = "")
    {
        public static CodeContextFileReadResult Missing { get; } = new(CodeContextFileReadStatus.Missing);

        public static CodeContextFileReadResult TooLarge { get; } = new(CodeContextFileReadStatus.TooLarge);

        public static CodeContextFileReadResult Empty { get; } = new(CodeContextFileReadStatus.Empty);

        public static CodeContextFileReadResult ReadFailed { get; } = new(CodeContextFileReadStatus.ReadFailed);
    }

    private enum CodeContextFileReadStatus
    {
        Read,
        Missing,
        TooLarge,
        Empty,
        ReadFailed
    }

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
