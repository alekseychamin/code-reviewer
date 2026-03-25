namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewPipelineOptions
{
    public const string SectionName = "ReviewPipeline";

    public int MaxChunkCharacters { get; init; } = 35000;

    public int MaxChangeSummaryCharacters { get; init; } = 12000;
}
