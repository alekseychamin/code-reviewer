using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Abstractions;

public interface ILlmStageRouter
{
    Task<StageRouteSelection> ResolveAsync(
        ReviewPipelineStage stage,
        string? requestedProfileId,
        bool localOnlyMode,
        IReadOnlyList<StageRouteOverrideDto> stageOverrides,
        CancellationToken cancellationToken);
}
