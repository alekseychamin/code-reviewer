using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IExternalReviewEngine
{
    Task<ExternalReviewArtifact> RunAsync(
        ReviewRun run,
        ExternalReviewInput? input,
        CancellationToken cancellationToken);
}
