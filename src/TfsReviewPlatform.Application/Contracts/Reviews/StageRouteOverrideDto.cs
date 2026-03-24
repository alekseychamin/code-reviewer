using System.ComponentModel.DataAnnotations;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class StageRouteOverrideDto
{
    [Required]
    public string Stage { get; init; } = string.Empty;

    [Required]
    public string ProfileId { get; init; } = string.Empty;

    public string? Model { get; init; }

    [Range(0, 2)]
    public double? Temperature { get; init; }
}
