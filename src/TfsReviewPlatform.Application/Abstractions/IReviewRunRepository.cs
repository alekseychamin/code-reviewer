using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewRunRepository
{
    Task AddAsync(ReviewRun run, CancellationToken cancellationToken);

    Task<ReviewRun?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewRun>> ListForTargetAsync(
        ReviewTargetDescriptor target,
        int limit,
        CancellationToken cancellationToken);

    Task DeleteForTargetAsync(
        ReviewTargetDescriptor target,
        CancellationToken cancellationToken);

    Task<ReviewRun?> FindLatestCompletedForTargetAsync(
        ReviewTargetDescriptor target,
        DateTimeOffset createdBefore,
        CancellationToken cancellationToken);

    Task UpdateAsync(ReviewRun run, CancellationToken cancellationToken);
}
