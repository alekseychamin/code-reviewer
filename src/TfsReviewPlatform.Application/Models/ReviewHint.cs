namespace TfsReviewPlatform.Application.Models;

public sealed record ReviewHint
{
    public required string RuleId { get; init; }

    public required string Category { get; init; }

    public required string FilePath { get; init; }

    public int StartLine { get; init; }

    public required string Message { get; init; }

    public string Evidence { get; init; } = string.Empty;

    public string SuggestedVerification { get; init; } = string.Empty;
}
