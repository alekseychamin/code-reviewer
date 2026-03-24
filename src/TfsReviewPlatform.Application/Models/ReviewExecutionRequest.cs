using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Models;

public sealed class ReviewExecutionRequest
{
    public required ReviewTargetKind TargetKind { get; init; }

    public required ReviewTargetDescriptor Target { get; init; }

    public string? ProviderProfileId { get; init; }

    public bool LocalOnlyMode { get; init; }

    public PublishMode PublishMode { get; init; }

    public string? AzureDevOpsAccessToken { get; init; }

    public IReadOnlyList<StageRouteOverrideDto> StageOverrides { get; init; } = [];
}
