using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewPromptFactory
{
    string BuildSystemPrompt(ReviewPipelineStage stage, string reviewContext);

    string BuildUserPrompt(ReviewPipelineStage stage, string payload);

    string BuildChunkReviewSystemPrompt(string reviewContext, string? additionalRules = null);
}
