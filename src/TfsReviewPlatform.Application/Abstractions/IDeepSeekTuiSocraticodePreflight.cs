using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IDeepSeekTuiSocraticodePreflight
{
    Task<DeepSeekTuiSocraticodePreflightResult> EnsureReadyAsync(
        string repositoryPath,
        string runDirectory,
        CancellationToken cancellationToken,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback = null);
}
