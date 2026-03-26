namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewHistoryDto
{
    public Guid? BaselineRunId { get; init; }

    public IReadOnlyList<ReviewHistoryItemDto> Items { get; init; } = [];
}
