using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IBackgroundReviewScheduler
{
    void Schedule(Guid runId, ReviewExecutionRequest request);
}
