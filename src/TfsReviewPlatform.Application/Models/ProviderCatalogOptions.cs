namespace TfsReviewPlatform.Application.Models;

public sealed class ProviderCatalogOptions
{
    public const string SectionName = "ProviderCatalog";

    public List<ProviderProfileOptions> Profiles { get; init; } = [];
}

public sealed class ProviderProfileOptions
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = "OpenAiCompatible";

    public string BaseUrl { get; init; } = string.Empty;

    public string DefaultModel { get; init; } = string.Empty;

    public bool SupportsStructuredOutput { get; init; } = true;

    public bool LocalOnly { get; init; }

    public string? ApiKey { get; init; }

    public string? ApiKeyEnvironmentVariable { get; init; }
}
