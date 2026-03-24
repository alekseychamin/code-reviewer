using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewRunExecutor
{
    Task ExecuteAsync(Guid runId, ReviewExecutionRequest request, CancellationToken cancellationToken);
}
