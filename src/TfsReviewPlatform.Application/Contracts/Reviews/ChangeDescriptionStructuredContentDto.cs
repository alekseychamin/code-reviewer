namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ChangeDescriptionStructuredContentDto
{
    public string Category { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> ImpactedModules { get; init; } = [];

    public IReadOnlyList<string> Risks { get; init; } = [];

    public int? EstimatedReviewEffort { get; init; }

    public int? QualityScore { get; init; }
}
