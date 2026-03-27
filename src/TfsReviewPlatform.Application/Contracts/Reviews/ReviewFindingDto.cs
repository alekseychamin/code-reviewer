using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ReviewFindingDto
{
    public string File { get; init; } = string.Empty;

    public string LineHint { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public FindingCategory Category { get; init; }

    public FindingSeverity Severity { get; init; }

    public ReviewFindingSource Source { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string ExistingCode { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;
}
