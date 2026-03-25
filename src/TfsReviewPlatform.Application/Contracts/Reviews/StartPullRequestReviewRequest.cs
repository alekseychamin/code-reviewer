using System.ComponentModel.DataAnnotations;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class StartPullRequestReviewRequest
{
    [Required]
    [Url]
    public string PullRequestUrl { get; init; } = string.Empty;

    public string? ProviderProfileId { get; init; }

    public PublishMode PublishMode { get; init; }

    public string? AzureDevOpsAccessToken { get; init; }

    public List<StageRouteOverrideDto> StageOverrides { get; init; } = [];
}
