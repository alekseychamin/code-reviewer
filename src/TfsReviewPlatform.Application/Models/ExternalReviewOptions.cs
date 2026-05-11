namespace TfsReviewPlatform.Application.Models;

public sealed class ExternalReviewOptions
{
    public const string SectionName = "ExternalReview";

    public bool Enabled { get; init; }

    public string EngineName { get; init; } = "PR-Agent";

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> Commands { get; init; } = [];

    public string ResponseLanguage { get; init; } = "ru-ru";

    public int TimeoutSeconds { get; init; } = 360;

    public int MaxWaitAtEndSeconds { get; init; } = 10;

    public bool UseChangeSummary { get; init; } = true;

    public int ChangeSummaryWaitSeconds { get; init; } = 45;

    public bool UseReviewFindings { get; init; } = true;
}
