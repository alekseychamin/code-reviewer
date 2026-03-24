using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed record StageRouteSelection(
    ProviderProfile Profile,
    string Model,
    double Temperature);
