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
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.Qdrant;

public sealed class QdrantReviewSemanticIndex(
    IHttpClientFactory httpClientFactory,
    IOptions<QdrantOptions> options,
    ILogger<QdrantReviewSemanticIndex> logger)
    : IReviewSemanticIndex
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

            return response?.Result?
                .Select(item => item.Payload)
                .Where(payload => payload is not null && !string.Equals(payload.RunId, run.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .Select(payload => payload!)
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
        [JsonPropertyName("payload")]
        public QdrantPayload? Payload { get; init; }
    }

    private sealed class QdrantPayload
    {
        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }

        [JsonPropertyName("chunk_text")]
        public string? ChunkText { get; init; }

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
