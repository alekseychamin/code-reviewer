using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Domain.Entities;

public sealed record ReviewProgressUpdate(
    Guid RunId,
    ReviewRunStatus Status,
    ReviewPipelineStage? Stage,
    int ProgressPercent,
    string Message,
    DateTimeOffset Timestamp,
    bool IsTerminal);
