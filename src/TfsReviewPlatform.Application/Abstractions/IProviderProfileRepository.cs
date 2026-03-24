using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IProviderProfileRepository
{
    Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken);

    Task<ProviderProfile?> GetByIdAsync(string profileId, CancellationToken cancellationToken);
}
