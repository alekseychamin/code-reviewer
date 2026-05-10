namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewPipelineOptions
{
    public const string SectionName = "ReviewPipeline";

    public int MaxChunkCharacters { get; init; } = 12000;

    public int MaxPrimaryReviewChunkCharacters { get; init; } = 35000;

    public bool MergePrimaryReviewChunks { get; init; } = false;

    public int MaxConcurrentChunkReviews { get; init; } = 3;

    public int MaxChangeSummaryCharacters { get; init; } = 12000;

    /// <summary>
    /// When true, primary review is a single LLM request with the full filtered diff plus full Roslyn graph JSON (no chunked primary review, no tools).
    /// </summary>
    public bool SinglePassFullDiffAndGraphPrimaryReview { get; init; }

    /// <summary>
    /// Maximum characters for diff + graph combined in single-pass mode; 0 means no truncation in-app.
    /// </summary>
    public int SinglePassFullContextMaxCharacters { get; init; }

    /// <summary>
    /// Include serialized Roslyn graph JSON in full-context primary review payload.
    /// </summary>
    public bool IncludeRoslynGraphInFullContextPayload { get; init; } = true;

    /// <summary>
    /// Max iterations for full-context tool loop (initial pass + up to N tool-assisted refinements).
    /// </summary>
    public int FullContextMaxToolIterations { get; init; } = 2;

    /// <summary>
    /// Final normalization pass over accumulated findings/opportunities to merge duplicates and return strict JSON schema.
    /// </summary>
    public bool EnableFinalModelNormalizationPass { get; init; } = true;

    public RoslynGraphPipelineOptions Roslyn { get; init; } = new();
}
