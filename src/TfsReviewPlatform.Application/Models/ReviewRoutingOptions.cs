namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewRoutingOptions
{
    public const string SectionName = "ReviewRouting";

    public string? DefaultProfileId { get; init; }

    public string? LocalOnlyProfileId { get; init; }

    public List<StageRouteOptions> Routes { get; init; } = [];
}

public sealed class StageRouteOptions
{
    public string Stage { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string? Model { get; init; }

    public double? Temperature { get; init; }
}
