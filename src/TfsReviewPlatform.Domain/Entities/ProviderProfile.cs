using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Domain.Entities;

public sealed record ProviderProfile(
    string Id,
    string Name,
    LlmProviderKind Kind,
    string BaseUrl,
    string DefaultModel,
    bool SupportsStructuredOutput,
    bool LocalOnly,
    string? ApiKey,
    string? ApiKeyEnvironmentVariable)
{
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        return string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
    }
}
