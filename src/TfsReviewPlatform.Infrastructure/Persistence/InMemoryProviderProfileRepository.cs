using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Infrastructure.Persistence;

public sealed class InMemoryProviderProfileRepository(IOptions<ProviderCatalogOptions> options)
    : IProviderProfileRepository
{
    private readonly Lazy<IReadOnlyList<ProviderProfile>> _profiles = new(() =>
    {
        if (options.Value.Profiles.Count == 0)
        {
            return
            [
                new ProviderProfile(
                    "openai-default",
                    "OpenAI Compatible",
                    LlmProviderKind.OpenAiCompatible,
                    "https://api.openai.com/v1",
                    "gpt-4.1-mini",
                    true,
                    false,
                    null,
                    "OPENAI_API_KEY"),
                new ProviderProfile(
                    "ollama-local",
                    "Local Ollama",
                    LlmProviderKind.Ollama,
                    "http://localhost:11434",
                    "qwen2.5-coder:14b",
                    false,
                    true,
                    null,
                    null)
            ];
        }

        return options.Value.Profiles.Select(profile => new ProviderProfile(
                profile.Id,
                profile.Name,
                Enum.Parse<LlmProviderKind>(profile.Kind, true),
                profile.BaseUrl,
                profile.DefaultModel,
                profile.SupportsStructuredOutput,
                profile.LocalOnly,
                profile.ApiKey,
                profile.ApiKeyEnvironmentVariable))
            .ToArray();
    });

    public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken)
        => Task.FromResult(_profiles.Value);

    public Task<ProviderProfile?> GetByIdAsync(string profileId, CancellationToken cancellationToken)
        => Task.FromResult(_profiles.Value.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)));
}
