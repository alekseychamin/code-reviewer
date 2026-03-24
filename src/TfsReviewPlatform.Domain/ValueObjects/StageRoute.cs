using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Domain.ValueObjects;

public sealed record StageRoute(
    ReviewPipelineStage Stage,
    string ProfileId,
    string? Model,
    double? Temperature);
