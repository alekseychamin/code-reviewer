using System.Collections.Concurrent;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Infrastructure.Persistence;

public sealed class InMemoryReviewRunRepository : IReviewRunRepository
{
    private readonly ConcurrentDictionary<Guid, ReviewRun> _runs = new();

    public Task AddAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<ReviewRun?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        _runs.TryGetValue(id, out var run);
        return Task.FromResult(run);
    }

    public Task UpdateAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }
}
