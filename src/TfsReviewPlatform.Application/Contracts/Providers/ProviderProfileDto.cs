using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Providers;

public sealed class ProviderProfileDto
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public LlmProviderKind Kind { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string DefaultModel { get; init; } = string.Empty;

    public bool SupportsStructuredOutput { get; init; }

    public bool LocalOnly { get; init; }
}
