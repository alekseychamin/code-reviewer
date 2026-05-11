using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IExternalReviewEngine
{
    Task<ExternalReviewArtifact> RunAsync(
        ReviewRun run,
        CancellationToken cancellationToken);
}
