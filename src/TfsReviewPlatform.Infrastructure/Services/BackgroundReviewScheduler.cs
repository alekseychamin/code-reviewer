using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Infrastructure.Services;

public sealed class BackgroundReviewScheduler(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<BackgroundReviewScheduler> logger)
    : IBackgroundReviewScheduler
{
    public void Schedule(Guid runId, ReviewExecutionRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IReviewRunExecutor>();
                await executor.ExecuteAsync(runId, request, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background execution failed for review run {RunId}", runId);
            }
        });
    }
}
