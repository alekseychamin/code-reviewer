using TfsReviewPlatform.Application.Models.Graph;

namespace TfsReviewPlatform.Application.Models;

public sealed record PreprocessedDiff
{
    public required string FilteredDiffText { get; init; }

    public required string ReviewContextDiffText { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> ReviewChunks { get; init; } = [];

    public IReadOnlyList<string> Chunks { get; init; } = [];

    public IReadOnlyList<ReviewHint> ReviewHints { get; init; } = [];

    public CodeGraph? Graph { get; init; }
}
