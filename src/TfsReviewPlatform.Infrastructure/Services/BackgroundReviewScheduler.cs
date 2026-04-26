using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Infrastructure.Services;

public sealed class BackgroundReviewScheduler(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<BackgroundReviewScheduler> logger)
    : IBackgroundReviewScheduler
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningRuns = new();

    public void Schedule(Guid runId, ReviewExecutionRequest request)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        _runningRuns[runId] = cancellationTokenSource;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IReviewRunExecutor>();
                await executor.ExecuteAsync(runId, request, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
            {
                logger.LogInformation("Background execution cancelled for review run {RunId}", runId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background execution failed for review run {RunId}", runId);
            }
            finally
            {
                if (_runningRuns.TryRemove(runId, out var source))
                {
                    source.Dispose();
                }
            }
        });
    }

    public bool Stop(Guid runId)
    {
        if (!_runningRuns.TryGetValue(runId, out var cancellationTokenSource))
        {
            return false;
        }

        cancellationTokenSource.Cancel();
        return true;
    }
}
