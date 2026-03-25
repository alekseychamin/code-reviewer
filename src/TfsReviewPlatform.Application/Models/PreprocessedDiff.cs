namespace TfsReviewPlatform.Application.Models;

public sealed class PreprocessedDiff
{
    public required string FilteredDiffText { get; init; }

    public required string ReviewContextDiffText { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> Chunks { get; init; } = [];
}
