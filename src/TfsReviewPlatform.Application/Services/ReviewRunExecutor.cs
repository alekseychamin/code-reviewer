using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRunExecutor(
    IReviewRunRepository reviewRunRepository,
    IReviewProgressStore reviewProgressStore,
    IPullRequestDiffService pullRequestDiffService,
    IBranchComparisonDiffService branchComparisonDiffService,
    IDiffPreprocessor diffPreprocessor,
    ILlmStageRouter llmStageRouter,
    ILlmCompletionService llmCompletionService,
    IReviewPromptFactory reviewPromptFactory,
    IFindingsNormalizer findingsNormalizer,
    IMarkdownReportBuilder markdownReportBuilder,
    IReviewPublisher reviewPublisher,
    ILogger<ReviewRunExecutor> logger)
    : IReviewRunExecutor
{
    public async Task ExecuteAsync(Guid runId, ReviewExecutionRequest request, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        try
        {
            run.Start();
            await PersistAndPublishAsync(run, "Review started", ReviewPipelineStage.DiffAcquisition, 2, cancellationToken);

            var diffResult = await AcquireDiffAsync(request, cancellationToken);
            await PersistAndPublishAsync(run, "Diff acquired", ReviewPipelineStage.Preprocessing, 15, cancellationToken);

            var preprocessed = diffPreprocessor.Process(diffResult.DiffText);
            await PersistAndPublishAsync(
                run,
                $"Prepared {preprocessed.ChangedFiles.Count} changed files across {preprocessed.Chunks.Count} chunks",
                ReviewPipelineStage.ChangeDescription,
                30,
                cancellationToken);

            var description = await GenerateChangeDescriptionAsync(run.Target.Title, preprocessed, request, cancellationToken);
            await PersistAndPublishAsync(run, "Change description generated", ReviewPipelineStage.ChunkReview, 45, cancellationToken);

            var rawFindings = await ReviewChunksAsync(description, preprocessed.Chunks, request, run, cancellationToken);
            await PersistAndPublishAsync(run, "Raw findings collected", ReviewPipelineStage.FindingsNormalization, 75, cancellationToken);

            var findings = findingsNormalizer.Normalize(rawFindings);
            await PersistAndPublishAsync(run, $"Normalized {findings.Count} findings", ReviewPipelineStage.FinalSynthesis, 87, cancellationToken);

            var fullReport = markdownReportBuilder.BuildFullReport(run.Target.Title, description, findings);
            var summaryComment = markdownReportBuilder.BuildSummaryComment(run.Target.Title, description, findings);
            var inlineComments = markdownReportBuilder.BuildInlineComments(findings, preprocessed.FilteredDiffText);

            var publishSucceeded = false;
            if (request.PublishMode != PublishMode.None &&
                request.TargetKind == ReviewTargetKind.PullRequest &&
                !string.IsNullOrWhiteSpace(request.AzureDevOpsAccessToken) &&
                !string.IsNullOrWhiteSpace(request.Target.PullRequestUrl))
            {
                await PersistAndPublishAsync(run, "Publishing review comments", ReviewPipelineStage.Publish, 95, cancellationToken);
                publishSucceeded = await reviewPublisher.PublishAsync(
                    request.Target.PullRequestUrl,
                    request.AzureDevOpsAccessToken,
                    request.PublishMode,
                    summaryComment,
                    inlineComments,
                    cancellationToken);
            }

            var artifacts = new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = description,
                MarkdownReport = fullReport,
                SummaryComment = summaryComment,
                InlineComments = inlineComments
            };

            run.Complete(artifacts, findings, publishSucceeded);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await reviewProgressStore.PublishAsync(
                new ReviewProgressUpdate(run.Id, run.Status, run.CurrentStage, run.ProgressPercent, run.CurrentMessage, DateTimeOffset.UtcNow, true),
                cancellationToken);
            reviewProgressStore.Complete(run.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Review run {RunId} failed", runId);
            run.Fail(exception.Message);
            await reviewRunRepository.UpdateAsync(run, CancellationToken.None);
            await reviewProgressStore.PublishAsync(
                new ReviewProgressUpdate(run.Id, run.Status, run.CurrentStage, run.ProgressPercent, exception.Message, DateTimeOffset.UtcNow, true),
                CancellationToken.None);
            reviewProgressStore.Complete(run.Id);
        }
    }

    private async Task<DiffAcquisitionResult> AcquireDiffAsync(
        ReviewExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return request.TargetKind switch
        {
            ReviewTargetKind.PullRequest when !string.IsNullOrWhiteSpace(request.AzureDevOpsAccessToken) &&
                                              !string.IsNullOrWhiteSpace(request.Target.PullRequestUrl) =>
                await pullRequestDiffService.GetDiffAsync(
                    request.Target.PullRequestUrl,
                    request.AzureDevOpsAccessToken,
                    cancellationToken),
            ReviewTargetKind.BranchComparison when !string.IsNullOrWhiteSpace(request.Target.RepositoryPath) =>
                await branchComparisonDiffService.GetDiffAsync(
                    request.Target.RepositoryPath,
                    request.Target.TargetBranch ?? string.Empty,
                    request.Target.SourceBranch ?? string.Empty,
                    request.Target.RepositoryName,
                    cancellationToken),
            _ => throw new InvalidOperationException("Review request does not contain enough information to acquire a diff.")
        };
    }

    private async Task<string> GenerateChangeDescriptionAsync(
        string reviewTitle,
        PreprocessedDiff preprocessed,
        ReviewExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChangeDescription,
            request.ProviderProfileId,
            request.LocalOnlyMode,
            request.StageOverrides,
            cancellationToken);

        var fileList = string.Join('\n', preprocessed.ChangedFiles.Take(100));
        var diffSnippet = preprocessed.FilteredDiffText.Length > 24000
            ? preprocessed.FilteredDiffText[..24000]
            : preprocessed.FilteredDiffText;

        return await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = selection.Temperature,
                SystemPrompt = reviewPromptFactory.BuildSystemPrompt(ReviewPipelineStage.ChangeDescription, reviewTitle),
                UserPrompt = reviewPromptFactory.BuildUserPrompt(
                    ReviewPipelineStage.ChangeDescription,
                    $"Changed files:\n{fileList}\n\nDiff snippet:\n{diffSnippet}")
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ReviewChunksAsync(
        string description,
        IReadOnlyList<string> chunks,
        ReviewExecutionRequest request,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            request.ProviderProfileId,
            request.LocalOnlyMode,
            request.StageOverrides,
            cancellationToken);

        var responses = new List<string>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var progress = 45 + (int)Math.Round(((index + 1d) / Math.Max(1, chunks.Count)) * 25d);
            await PersistAndPublishAsync(
                run,
                $"Reviewing chunk {index + 1} of {chunks.Count}",
                ReviewPipelineStage.ChunkReview,
                progress,
                cancellationToken);

            var response = await llmCompletionService.CompleteAsync(
                selection.Profile,
                new LlmChatRequest
                {
                    Model = selection.Model,
                    Temperature = selection.Temperature,
                    ExpectJson = true,
                    SystemPrompt = reviewPromptFactory.BuildSystemPrompt(ReviewPipelineStage.ChunkReview, description),
                    UserPrompt = reviewPromptFactory.BuildUserPrompt(ReviewPipelineStage.ChunkReview, chunks[index])
                },
                cancellationToken);

            responses.Add(response);
        }

        return responses;
    }

    private async Task PersistAndPublishAsync(
        ReviewRun run,
        string message,
        ReviewPipelineStage stage,
        int percent,
        CancellationToken cancellationToken)
    {
        run.Advance(stage, percent, message);
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        await reviewProgressStore.PublishAsync(
            new ReviewProgressUpdate(run.Id, run.Status, stage, percent, message, DateTimeOffset.UtcNow, false),
            cancellationToken);
    }
}
