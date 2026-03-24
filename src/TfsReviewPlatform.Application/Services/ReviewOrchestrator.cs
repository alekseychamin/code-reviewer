using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewOrchestrator(
    IReviewRunRepository reviewRunRepository,
    IReviewRequestValidator reviewRequestValidator,
    IBackgroundReviewScheduler backgroundReviewScheduler,
    IReviewProgressStore reviewProgressStore,
    ILogger<ReviewOrchestrator> logger)
    : IReviewOrchestrator
{
    public async Task<ReviewRunDto> StartPullRequestReviewAsync(
        StartPullRequestReviewRequest request,
        CancellationToken cancellationToken)
    {
        Validate(reviewRequestValidator.Validate(request));

        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.PullRequest,
            request.PullRequestUrl,
            request.PullRequestUrl,
            null,
            null,
            null,
            null);

        var executionRequest = new ReviewExecutionRequest
        {
            TargetKind = ReviewTargetKind.PullRequest,
            Target = target,
            ProviderProfileId = request.ProviderProfileId,
            LocalOnlyMode = request.LocalOnlyMode,
            PublishMode = request.PublishMode,
            AzureDevOpsAccessToken = ResolveAzureDevOpsToken(request.AzureDevOpsAccessToken),
            StageOverrides = request.StageOverrides
        };

        return await EnqueueAsync(target, executionRequest, cancellationToken);
    }

    public async Task<ReviewRunDto> StartBranchReviewAsync(
        StartBranchReviewRequest request,
        CancellationToken cancellationToken)
    {
        Validate(reviewRequestValidator.Validate(request));

        var title = $"{request.RepositoryName ?? Path.GetFileName(request.RepositoryPath)}: {request.SourceBranch} -> {request.TargetBranch}";
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.BranchComparison,
            title,
            null,
            request.RepositoryPath,
            request.RepositoryName ?? Path.GetFileName(request.RepositoryPath),
            request.SourceBranch,
            request.TargetBranch);

        var executionRequest = new ReviewExecutionRequest
        {
            TargetKind = ReviewTargetKind.BranchComparison,
            Target = target,
            ProviderProfileId = request.ProviderProfileId,
            LocalOnlyMode = request.LocalOnlyMode,
            PublishMode = request.PublishMode,
            StageOverrides = request.StageOverrides
        };

        return await EnqueueAsync(target, executionRequest, cancellationToken);
    }

    public async Task<ReviewRunDto?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken);
        return run?.ToDto();
    }

    private async Task<ReviewRunDto> EnqueueAsync(
        ReviewTargetDescriptor target,
        ReviewExecutionRequest executionRequest,
        CancellationToken cancellationToken)
    {
        var run = new ReviewRun(Guid.NewGuid(), target, executionRequest.ProviderProfileId, executionRequest.LocalOnlyMode);
        await reviewRunRepository.AddAsync(run, cancellationToken);
        reviewProgressStore.EnsureRun(run.Id);

        logger.LogInformation("Queued review run {RunId} for {TargetTitle}", run.Id, target.Title);
        backgroundReviewScheduler.Schedule(run.Id, executionRequest);

        return run.ToDto();
    }

    private static void Validate(IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(string.Join("; ", errors.SelectMany(item => item.Value.Select(message => $"{item.Key}: {message}"))));
    }

    private static string? ResolveAzureDevOpsToken(string? requestToken)
    {
        return !string.IsNullOrWhiteSpace(requestToken)
            ? requestToken
            : Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
    }
}
