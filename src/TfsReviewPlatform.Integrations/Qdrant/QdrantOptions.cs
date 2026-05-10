namespace TfsReviewPlatform.Integrations.Qdrant;

public sealed class QdrantOptions
{
    public bool Enabled { get; init; }

    public string Url { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string CollectionName { get; init; } = "review_semantic_index";

    public int VectorSize { get; init; } = 1024;

    public int TargetedTopK { get; init; } = 4;

    public int GeneralTopK { get; init; } = 8;

    public double HistoricalFindingHalfLifeDays { get; init; } = 45;

    public string EmbeddingBaseUrl { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = "bge-m3:latest";

    public bool CodeContextEnabled { get; init; } = true;

    public int CodeContextMaxSourceFiles { get; init; } = 600;

    public int CodeContextMaxTargetFiles { get; init; } = 160;

    public int CodeContextMaxFileBytes { get; init; } = 120_000;

    public int CodeContextChunkMaxLines { get; init; } = 120;

    public int CodeContextChunkOverlapLines { get; init; } = 20;

    public int CodeContextMaxQueries { get; init; } = 8;

    public int CodeContextTopKPerQuery { get; init; } = 3;

    public int CodeContextMaxSnippets { get; init; } = 10;
}
