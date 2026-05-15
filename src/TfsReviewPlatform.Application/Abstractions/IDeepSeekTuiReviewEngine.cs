using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IDeepSeekTuiReviewEngine
{
    Task<DeepSeekTuiReviewResult> RunAsync(
        ReviewRun run,
        ExternalReviewInput input,
        CancellationToken cancellationToken,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback = null);
}
