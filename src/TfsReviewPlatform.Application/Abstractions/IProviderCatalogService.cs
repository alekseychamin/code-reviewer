using TfsReviewPlatform.Application.Contracts.Providers;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IProviderCatalogService
{
    Task<IReadOnlyList<ProviderProfileDto>> ListAsync(CancellationToken cancellationToken);
}
