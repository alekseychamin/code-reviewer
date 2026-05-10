using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewPromptFactory
{
    string BuildSystemPrompt(ReviewPipelineStage stage, string reviewContext);

    string BuildUserPrompt(ReviewPipelineStage stage, string payload);

    string BuildChunkReviewSystemPrompt(string reviewContext, string? additionalRules = null);

    /// <summary>
    /// Primary review in one shot over full diff + graph JSON.
    /// </summary>
    string BuildSinglePassPrimaryReviewSystemPrompt(string reviewContext);

    string BuildSinglePassPrimaryReviewUserPrompt(string diffAndGraphPayload);

    string BuildDeterministicCoverageCriticSystemPrompt(string reviewContext);

    string BuildDeterministicCoverageCriticUserPrompt(
        string deterministicHintsBlock,
        string deterministicContextBlock,
        string primaryReviewResponse);

    string BuildFinalNormalizationSystemPrompt();

    string BuildFinalNormalizationUserPrompt(
        IReadOnlyList<Domain.Entities.ReviewFinding> findings,
        IReadOnlyList<Domain.Entities.ReviewOpportunityItem> opportunities);
}
