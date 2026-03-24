using Microsoft.Extensions.Logging;
using System.Text.Json;
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
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(
                run,
                $"Prepared {preprocessed.ChangedFiles.Count} changed files across {preprocessed.Chunks.Count} chunks",
                ReviewPipelineStage.ChangeDescription,
                30,
                cancellationToken);

            var changeSummary = await GenerateChangeSummaryAsync(run.Target.Title, preprocessed, request, cancellationToken);
            var description = changeSummary.Description;
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Change description generated", ReviewPipelineStage.ChunkReview, 45, cancellationToken);

            var rawFindings = await ReviewChunksAsync(description, preprocessed.Chunks, request, run, cancellationToken);
            await PersistAndPublishAsync(run, "Raw findings collected", ReviewPipelineStage.FindingsNormalization, 75, cancellationToken);

            var findings = findingsNormalizer.Normalize(rawFindings);
            run.UpdateFindings(findings);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, $"Normalized {findings.Count} findings", ReviewPipelineStage.FinalSynthesis, 87, cancellationToken);

            var fullReport = markdownReportBuilder.BuildFullReport(run.Target.Title, description, findings);
            var summaryComment = markdownReportBuilder.BuildSummaryComment(run.Target.Title, description, findings);
            var inlineComments = markdownReportBuilder.BuildInlineComments(findings, preprocessed.FilteredDiffText);
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                MarkdownReport = fullReport,
                SummaryComment = summaryComment,
                InlineComments = inlineComments
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Final report generated", ReviewPipelineStage.FinalSynthesis, 93, cancellationToken);

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
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
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

    private async Task<ChangeSummaryResult> GenerateChangeSummaryAsync(
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

        var response = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = selection.Temperature,
                ExpectJson = true,
                SystemPrompt = reviewPromptFactory.BuildSystemPrompt(ReviewPipelineStage.ChangeDescription, reviewTitle),
                UserPrompt = reviewPromptFactory.BuildUserPrompt(
                    ReviewPipelineStage.ChangeDescription,
                    $"Changed files:\n{fileList}\n\nDiff snippet:\n{diffSnippet}")
            },
            cancellationToken);

        var parsed = ParseChangeSummary(response);
        if (!string.IsNullOrWhiteSpace(parsed.DiagramMermaid))
        {
            return parsed;
        }

        var diagram = await GenerateChangeDiagramAsync(selection.Profile, selection.Model, selection.Temperature, preprocessed, parsed.Description, cancellationToken);
        return new ChangeSummaryResult
        {
            Description = parsed.Description,
            DiagramMermaid = diagram
        };
    }

    private static ChangeSummaryResult ParseChangeSummary(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new ChangeSummaryResult
            {
                Description = "Автоматическое описание изменений недоступно."
            };
        }

        var payload = response.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
        {
            payload = payload
                .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var description = root.TryGetProperty("description", out var descriptionNode)
                ? descriptionNode.GetString()
                : null;
            var diagram = root.TryGetProperty("diagram", out var diagramNode)
                ? diagramNode.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(description))
            {
                return new ChangeSummaryResult
                {
                    Description = description,
                    DiagramMermaid = NormalizeMermaidCode(diagram)
                };
            }
        }
        catch (JsonException)
        {
        }

        return new ChangeSummaryResult
        {
            Description = response
        };
    }

    private static string? NormalizeMermaidCode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = content.Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            normalized = normalized
                .Replace("```mermaid", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        var graphIndex = normalized.IndexOf("graph", StringComparison.OrdinalIgnoreCase);
        var flowchartIndex = normalized.IndexOf("flowchart", StringComparison.OrdinalIgnoreCase);
        var startIndex = graphIndex >= 0 && flowchartIndex >= 0
            ? Math.Min(graphIndex, flowchartIndex)
            : Math.Max(graphIndex, flowchartIndex);

        if (startIndex > 0)
        {
            normalized = normalized[startIndex..].Trim();
        }

        return normalized.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    private async Task<string?> GenerateChangeDiagramAsync(
        ProviderProfile profile,
        string model,
        double temperature,
        PreprocessedDiff preprocessed,
        string description,
        CancellationToken cancellationToken)
    {
        var fileList = string.Join('\n', preprocessed.ChangedFiles.Take(100));
        var diffSnippet = preprocessed.FilteredDiffText.Length > 18000
            ? preprocessed.FilteredDiffText[..18000]
            : preprocessed.FilteredDiffText;

        var response = await llmCompletionService.CompleteAsync(
            profile,
            new LlmChatRequest
            {
                Model = model,
                Temperature = temperature,
                SystemPrompt = """
                    You are a Lead System Architect.
                    Produce only Mermaid code for a concise semantic change diagram.
                    Prefer "flowchart LR".
                    Show only meaningful changed components, handlers, endpoints, and dependencies.
                    Return an empty response if a diagram is not useful.
                    """,
                UserPrompt =
                    $"Description:\n{description}\n\nChanged files:\n{fileList}\n\nDiff snippet:\n{diffSnippet}"
            },
            cancellationToken);

        return NormalizeMermaidCode(response);
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
