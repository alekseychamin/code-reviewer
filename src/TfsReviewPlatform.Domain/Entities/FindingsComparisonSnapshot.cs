namespace TfsReviewPlatform.Domain.Entities;

public sealed class FindingsComparisonSnapshot
{
    public Guid? PreviousRunId { get; init; }

    public int PreviousFindingsCount { get; init; }

    public int CurrentFindingsCount { get; init; }

    public int NewFindingsCount { get; init; }

    public int StillRelevantFindingsCount { get; init; }

    public int ResolvedFindingsCount { get; init; }

    public IReadOnlyList<ReviewFinding> NewFindings { get; init; } = [];

    public IReadOnlyList<ReviewFinding> StillRelevantFindings { get; init; } = [];

    public IReadOnlyList<ReviewFinding> ResolvedFindings { get; init; } = [];
}
