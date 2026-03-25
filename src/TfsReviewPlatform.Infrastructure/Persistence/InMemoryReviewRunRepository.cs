using System.Collections.Concurrent;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

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

    public Task<ReviewRun?> FindLatestCompletedForTargetAsync(
        ReviewTargetDescriptor target,
        DateTimeOffset createdBefore,
        CancellationToken cancellationToken)
    {
        var run = _runs.Values
            .Where(candidate => candidate.Target == target)
            .Where(candidate => candidate.Status == ReviewRunStatus.Completed)
            .Where(candidate => candidate.CreatedAt < createdBefore)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(run);
    }

    public Task UpdateAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }
}
