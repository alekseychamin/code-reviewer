namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewPipelineOptions
{
    public const string SectionName = "ReviewPipeline";

    public int MaxChunkCharacters { get; init; } = 12000;

    public int MaxPrimaryReviewChunkCharacters { get; init; } = 35000;

    public bool MergePrimaryReviewChunks { get; init; } = false;

    public int MaxConcurrentChunkReviews { get; init; } = 3;

    public int MaxChangeSummaryCharacters { get; init; } = 12000;
}
