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

    public Task<IReadOnlyList<ReviewRun>> ListForTargetAsync(
        ReviewTargetDescriptor target,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ReviewRun> runs = _runs.Values
            .Where(candidate => BuildTargetKey(candidate.Target) == BuildTargetKey(target))
            .OrderByDescending(candidate => candidate.CreatedAt)
            .Take(limit)
            .ToArray();

        return Task.FromResult(runs);
    }

    public Task DeleteForTargetAsync(
        ReviewTargetDescriptor target,
        CancellationToken cancellationToken)
    {
        var targetKey = BuildTargetKey(target);
        foreach (var entry in _runs)
        {
            if (BuildTargetKey(entry.Value.Target) == targetKey)
            {
                _runs.TryRemove(entry.Key, out _);
            }
        }

        return Task.CompletedTask;
    }

    public Task<ReviewRun?> FindLatestCompletedForTargetAsync(
        ReviewTargetDescriptor target,
        DateTimeOffset createdBefore,
        CancellationToken cancellationToken)
    {
        var run = _runs.Values
            .Where(candidate => BuildTargetKey(candidate.Target) == BuildTargetKey(target))
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

    private static string BuildTargetKey(ReviewTargetDescriptor target)
    {
        return target.Kind switch
        {
            ReviewTargetKind.PullRequest => $"pr|{NormalizeTargetPart(target.PullRequestUrl)}",
            ReviewTargetKind.BranchComparison =>
                $"branches|{NormalizeTargetPart(target.RepositoryPath)}|{NormalizeTargetPart(target.SourceBranch)}|{NormalizeTargetPart(target.TargetBranch)}",
            _ => $"{target.Kind}|{target.Title}"
        };
    }

    private static string NormalizeTargetPart(string? value)
        => (value ?? string.Empty).Trim();
}
