using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewProgressEventDto
{
    public Guid RunId { get; init; }

    public ReviewRunStatus Status { get; init; }

    public ReviewPipelineStage? Stage { get; init; }

    public int ProgressPercent { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; }

    public bool IsTerminal { get; init; }
}
