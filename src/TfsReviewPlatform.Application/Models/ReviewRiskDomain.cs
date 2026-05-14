namespace TfsReviewPlatform.Application.Models;

public enum ReviewRiskDomain
{
    AuthTokenSecurity,
    ConfigDiOptions,
    HttpIntegration,
    CacheStateTtl,
    SqlEfDataIntegrity,
    KafkaCdcEvents,
    ApiContractValidation,
    TestsFixtures,
    BackgroundJobsConcurrency,
    ObservabilityOperability,
    SerializationMapping,
    FrontendUiState,
    BuildPackaging
}

public sealed record ReviewRiskDomainInsight
{
    public required ReviewRiskDomain Domain { get; init; }

    public required string Title { get; init; }

    public required string ReviewMode { get; init; }

    public int Score { get; init; }

    public IReadOnlyList<ReviewRiskFactor> Factors { get; init; } = [];

    public IReadOnlyList<string> Checklist { get; init; } = [];
}

public sealed record ReviewRiskFactor
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string FilePath { get; init; }

    public int StartLine { get; init; }

    public required string Evidence { get; init; }

    public int Weight { get; init; }
}
