namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class FindingsComparisonDto
{
    public Guid? PreviousRunId { get; init; }

    public int PreviousFindingsCount { get; init; }

    public int CurrentFindingsCount { get; init; }

    public int NewFindingsCount { get; init; }

    public int StillRelevantFindingsCount { get; init; }

    public int ResolvedFindingsCount { get; init; }

    public IReadOnlyList<ReviewFindingDto> NewFindings { get; init; } = [];

    public IReadOnlyList<ReviewFindingDto> StillRelevantFindings { get; init; } = [];

    public IReadOnlyList<ReviewFindingDto> ResolvedFindings { get; init; } = [];
}
