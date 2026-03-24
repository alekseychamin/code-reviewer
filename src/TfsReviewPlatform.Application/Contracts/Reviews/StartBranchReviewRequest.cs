using System.ComponentModel.DataAnnotations;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class StartBranchReviewRequest
{
    [Required]
    public string RepositoryPath { get; init; } = string.Empty;

    [Required]
    public string TargetBranch { get; init; } = string.Empty;

    [Required]
    public string SourceBranch { get; init; } = string.Empty;

    public string? RepositoryName { get; init; }

    public string? ProviderProfileId { get; init; }

    public bool LocalOnlyMode { get; init; }

    public PublishMode PublishMode { get; init; }

    public List<StageRouteOverrideDto> StageOverrides { get; init; } = [];
}
