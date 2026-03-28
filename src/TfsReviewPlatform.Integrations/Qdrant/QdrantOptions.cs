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

    public string EmbeddingBaseUrl { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = "bge-m3:latest";
}
