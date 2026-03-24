using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Providers;

namespace TfsReviewPlatform.Application.Services;

public sealed class ProviderCatalogService(IProviderProfileRepository providerProfileRepository)
    : IProviderCatalogService
{
    public async Task<IReadOnlyList<ProviderProfileDto>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await providerProfileRepository.ListAsync(cancellationToken);

        return profiles
            .Select(profile => new ProviderProfileDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Kind = profile.Kind,
                BaseUrl = profile.BaseUrl,
                DefaultModel = profile.DefaultModel,
                SupportsStructuredOutput = profile.SupportsStructuredOutput,
                LocalOnly = profile.LocalOnly
            })
            .OrderBy(profile => profile.LocalOnly)
            .ThenBy(profile => profile.Name)
            .ToArray();
    }
}
