namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewPipelineOptions
{
    public const string SectionName = "ReviewPipeline";

    public int MaxChunkCharacters { get; init; } = 12000;

    public int MaxPrimaryReviewChunkCharacters { get; init; } = 35000;

    public bool MergePrimaryReviewChunks { get; init; } = false;

    public int MaxConcurrentChunkReviews { get; init; } = 5;

    /// <summary>
    /// Maximum workspace tool requests allowed for one primary chunk review. Set to 0 to disable chunk tool-loop refinements.
    /// </summary>
    public int MaxChunkToolRequests { get; init; } = 1;

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
    /// Set to 0 to skip tool-assisted refinements after the initial full-context response.
    /// </summary>
    public int FullContextMaxToolIterations { get; init; } = 2;

    /// <summary>
    /// When true, an extra LLM pass checks whether deterministic hints are covered by the primary review.
    /// </summary>
    public bool EnableDeterministicCoverageCritic { get; init; } = true;

    /// <summary>
    /// Final normalization pass over accumulated findings/opportunities to merge duplicates and return strict JSON schema.
    /// </summary>
    public bool EnableFinalModelNormalizationPass { get; init; } = true;

    /// <summary>
    /// When true, suppress findings that cannot be anchored to the changed diff file, hunk, or code evidence.
    /// </summary>
    public bool EnableFindingEvidenceGate { get; init; } = true;

    /// <summary>
    /// When true and external review is enabled, use PR-Agent as a fast scout and run focused missing-finding critics instead of the primary full/chunk review.
    /// </summary>
    public bool UseExternalReviewScoutMode { get; init; }

    /// <summary>
    /// When true, repeated reviews with a baseline analyze only changed diff sections while keeping baseline findings for comparison.
    /// </summary>
    public bool UseIncrementalReviewMode { get; init; } = true;

    public int ExternalReviewScoutMaxCriticCharacters { get; init; } = 26000;

    public int MaxConcurrentExternalReviewScoutCritics { get; init; } = 3;

    public RoslynGraphPipelineOptions Roslyn { get; init; } = new();
}
