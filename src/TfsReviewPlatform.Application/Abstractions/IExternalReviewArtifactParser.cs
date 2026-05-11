using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IExternalReviewArtifactParser
{
    ExternalReviewInsights Parse(ExternalReviewArtifact artifact);
}
