using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class LlmStageRouter(
    IProviderProfileRepository providerProfileRepository,
    IOptions<ReviewRoutingOptions> routingOptions)
    : ILlmStageRouter
{
    public async Task<StageRouteSelection> ResolveAsync(
        ReviewPipelineStage stage,
        string? requestedProfileId,
        bool localOnlyMode,
        IReadOnlyList<StageRouteOverrideDto> stageOverrides,
        CancellationToken cancellationToken)
    {
        var overrideRoute = stageOverrides.FirstOrDefault(route =>
            string.Equals(route.Stage, stage.ToString(), StringComparison.OrdinalIgnoreCase));

        var configuredRoute = routingOptions.Value.Routes.FirstOrDefault(route =>
            string.Equals(route.Stage, stage.ToString(), StringComparison.OrdinalIgnoreCase));

        var selectedProfileId = overrideRoute?.ProfileId
            ?? requestedProfileId
            ?? (localOnlyMode ? routingOptions.Value.LocalOnlyProfileId : null)
            ?? configuredRoute?.ProfileId
            ?? routingOptions.Value.DefaultProfileId;

        if (string.IsNullOrWhiteSpace(selectedProfileId))
        {
            throw new InvalidOperationException("No provider profile is configured for the requested review stage.");
        }

        var profile = await providerProfileRepository.GetByIdAsync(selectedProfileId, cancellationToken);
        if (profile is null)
        {
            throw new InvalidOperationException($"Provider profile '{selectedProfileId}' was not found.");
        }

        EnsureLocalOnlyCompatibility(localOnlyMode, profile);

        var model = overrideRoute?.Model ?? configuredRoute?.Model ?? profile.DefaultModel;
        var temperature = overrideRoute?.Temperature ?? configuredRoute?.Temperature ?? 0;

        return new StageRouteSelection(profile, model, temperature);
    }

    private static void EnsureLocalOnlyCompatibility(bool localOnlyMode, ProviderProfile profile)
    {
        if (localOnlyMode && !profile.LocalOnly)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Id}' is not marked as local-only and cannot be used in local-only mode.");
        }
    }
}
