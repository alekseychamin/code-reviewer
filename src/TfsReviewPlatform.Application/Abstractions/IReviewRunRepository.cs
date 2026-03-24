using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewRunRepository
{
    Task AddAsync(ReviewRun run, CancellationToken cancellationToken);

    Task<ReviewRun?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task UpdateAsync(ReviewRun run, CancellationToken cancellationToken);
}
