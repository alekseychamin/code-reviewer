using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Prompts;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRunExecutor(
    IReviewRunRepository reviewRunRepository,
    IReviewProgressStore reviewProgressStore,
    IReviewSemanticIndex reviewSemanticIndex,
    IPullRequestDiffService pullRequestDiffService,
    IBranchComparisonDiffService branchComparisonDiffService,
    IDiffPreprocessor diffPreprocessor,
    IRepositoryFileContentService repositoryFileContentService,
    ILlmStageRouter llmStageRouter,
    ILlmCompletionService llmCompletionService,
    IReviewPromptFactory reviewPromptFactory,
    IChunkReviewResponseParser chunkReviewResponseParser,
    IRoslynWorkspaceBootstrapper roslynWorkspaceBootstrapper,
    IRoslynGraphBuilder roslynGraphBuilder,
    IGraphAwareChunker graphAwareChunker,
    IReviewWorkspaceToolExecutor reviewWorkspaceToolExecutor,
    IFindingsComparisonService findingsComparisonService,
    IMarkdownReportBuilder markdownReportBuilder,
    IReviewPublisher reviewPublisher,
    IOptions<ReviewPipelineOptions> reviewPipelineOptions,
    ILogger<ReviewRunExecutor> logger)
    : IReviewRunExecutor
{
    public async Task ExecuteAsync(Guid runId, ReviewExecutionRequest request, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, CancellationToken.None)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");
        if (run.Status == ReviewRunStatus.Cancelled)
        {
            await reviewProgressStore.PublishAsync(
                new ReviewProgressUpdate(run.Id, run.Status, run.CurrentStage, run.ProgressPercent, run.CurrentMessage, DateTimeOffset.UtcNow, true),
                CancellationToken.None);
            reviewProgressStore.Complete(run.Id);
            return;
        }

        ReviewRun? previousRun = null;
        DiffAcquisitionResult? diffResult = null;
        string? roslynCleanupDirectory = null;
        var mandatoryFindings = new List<ReviewFinding>();

        try
        {
            run.Start();
            await PersistAndPublishAsync(run, "Review started", ReviewPipelineStage.DiffAcquisition, 2, cancellationToken);
            LogReviewPipelineConfiguration(run, request);
            previousRun = await ResolveBaselineRunAsync(request, run, cancellationToken);

            diffResult = await AcquireDiffAsync(request, cancellationToken);
            run.UpdateMetadata(diffResult.ServiceName, diffResult.AuthorName, diffResult.PullRequestTitle);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Diff acquired", ReviewPipelineStage.Preprocessing, 15, cancellationToken);

            var preprocessed = diffPreprocessor.Process(diffResult.DiffText);

            var pipelineOptsForRoslyn = reviewPipelineOptions.Value;
            if (pipelineOptsForRoslyn.Roslyn.Enabled)
            {
                await PersistAndPublishAsync(
                    run,
                    "Ход выполнения пайплайна: подготовка Roslyn workspace для построения graph",
                    ReviewPipelineStage.Preprocessing,
                    18,
                    cancellationToken);

                var bootstrap = await roslynWorkspaceBootstrapper.TryPrepareAsync(
                    run.Id,
                    diffResult,
                    request.PullRequestAccessToken,
                    preprocessed.ChangedFiles,
                    cancellationToken);
                if (bootstrap is { Success: true, WorkspaceDirectory: { } ws, SolutionPath: { } sln })
                {
                    roslynCleanupDirectory = bootstrap.CleanupDirectory;

                    await PersistAndPublishAsync(
                        run,
                        "Ход выполнения пайплайна: построение graph (RoslynGraphBuilder)",
                        ReviewPipelineStage.Preprocessing,
                        22,
                        cancellationToken);

                    var graph = await roslynGraphBuilder.BuildAsync(ws, sln, preprocessed.ChangedFiles, cancellationToken);
                    preprocessed = await graphAwareChunker.AugmentWithGraphChunksAsync(
                        preprocessed,
                        graph,
                        ws,
                        pipelineOptsForRoslyn,
                        cancellationToken);

                    await PersistAndPublishAsync(
                        run,
                        "Ход выполнения пайплайна: graph построен, расширяем чанки контекстом",
                        ReviewPipelineStage.Preprocessing,
                        27,
                        cancellationToken);
                }
                else
                {
                    var bootstrapFinding = BuildRoslynBootstrapFailureFinding(bootstrap);
                    if (bootstrapFinding is not null)
                    {
                        mandatoryFindings.Add(bootstrapFinding);
                    }

                    await PersistAndPublishAsync(
                        run,
                        "Ход выполнения пайплайна: построение graph пропущено, используем diff-only чанки",
                        ReviewPipelineStage.Preprocessing,
                        22,
                        cancellationToken);
                }
            }

            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                PreparedChunks = preprocessed.Chunks,
                ChangedFiles = preprocessed.ChangedFiles
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(
                run,
                $"Подготовлено {preprocessed.ChangedFiles.Count} изменённых файлов в {preprocessed.ReviewChunks.Count} чанках ревью",
                ReviewPipelineStage.ChangeDescription,
                30,
                cancellationToken);

            if (!request.ForceRerun && previousRun is not null && HasNoChangesSincePreviousReview(previousRun, preprocessed))
            {
                var reusedComparison = findingsComparisonService.CompareUnchangedDiff(
                    previousRun.Id,
                    previousRun.Findings,
                    previousRun.Findings);
                var reusedArtifacts = new ReviewArtifacts
                {
                    DiffText = previousRun.Artifacts.DiffText,
                    PreparedChunks = previousRun.Artifacts.PreparedChunks,
                    ChangedFiles = previousRun.Artifacts.ChangedFiles,
                    ChangeDescription = previousRun.Artifacts.ChangeDescription,
                    ChangeDescriptionStructured = previousRun.Artifacts.ChangeDescriptionStructured,
                    ChangeDiagramMermaid = previousRun.Artifacts.ChangeDiagramMermaid,
                    MarkdownReport = previousRun.Artifacts.MarkdownReport,
                    SummaryComment = previousRun.Artifacts.SummaryComment,
                    ReviewDiscussionMessages = previousRun.Artifacts.ReviewDiscussionMessages,
                    InlineComments = previousRun.Artifacts.InlineComments,
                    ReviewedFiles = previousRun.Artifacts.ReviewedFiles,
                    PrimaryOpportunities = previousRun.Artifacts.PrimaryOpportunities,
                    FindingsComparison = reusedComparison
                };

                run.UpdateArtifacts(reusedArtifacts);
                run.UpdateFindings(previousRun.Findings);
                await reviewRunRepository.UpdateAsync(run, cancellationToken);
                await PersistAndPublishAsync(
                    run,
                    "Новых изменений с прошлого завершённого ревью не найдено. Используем сохранённый результат.",
                    ReviewPipelineStage.FinalSynthesis,
                    95,
                    cancellationToken);

                run.Complete(reusedArtifacts, previousRun.Findings, false);
                var reusedCompletedUpdate = new ReviewProgressUpdate(
                    run.Id,
                    run.Status,
                    run.CurrentStage,
                    run.ProgressPercent,
                    run.CurrentMessage,
                    DateTimeOffset.UtcNow,
                    true);
                run.RecordProgress(reusedCompletedUpdate);
                await reviewRunRepository.UpdateAsync(run, cancellationToken);
                await TryIndexSemanticArtifactsAsync(run, cancellationToken);
                await reviewProgressStore.PublishAsync(reusedCompletedUpdate, cancellationToken);
                reviewProgressStore.Complete(run.Id);
                return;
            }

            var changeSummary = await GenerateChangeSummaryAsync(run, run.DisplayTitle, preprocessed, request, cancellationToken);
            var description = changeSummary.Description;
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                PreparedChunks = preprocessed.Chunks,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDescriptionStructured = changeSummary.StructuredContent,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Change description generated", ReviewPipelineStage.ChunkReview, 45, cancellationToken);

            var rawFindings = reviewPipelineOptions.Value.SinglePassFullDiffAndGraphPrimaryReview
                ? await ReviewSinglePassFullContextAsync(description, preprocessed, diffResult, request, run, cancellationToken)
                : await ReviewChunksAsync(description, preprocessed.ReviewChunks, diffResult, request, run, cancellationToken);
            await PersistAndPublishAsync(run, "Raw findings collected", ReviewPipelineStage.FindingsNormalization, 75, cancellationToken);

            var findings = new List<ReviewFinding>();
            var opportunities = new List<ReviewOpportunityItem>();
            foreach (var raw in rawFindings)
            {
                var parsed = chunkReviewResponseParser.ParseChunkResponse(raw);
                findings.AddRange(parsed.Findings);
                opportunities.AddRange(parsed.Opportunities);
            }

            findings.AddRange(mandatoryFindings.Where(finding =>
                findings.All(existing => !CoversFinding(existing, finding))));
            if (mandatoryFindings.Count > 0)
            {
                logger.LogInformation(
                    "Added {FindingCount} mandatory pipeline findings for run {RunId}",
                    mandatoryFindings.Count,
                    run.Id);
            }

            var confirmedDeterministicFindings = BuildConfirmedDeterministicFindings(
                preprocessed.ReviewHints,
                findings);
            findings.AddRange(confirmedDeterministicFindings);
            if (confirmedDeterministicFindings.Count > 0)
            {
                logger.LogInformation(
                    "Added {FindingCount} confirmed deterministic findings for run {RunId}",
                    confirmedDeterministicFindings.Count,
                    run.Id);
            }

            if (reviewPipelineOptions.Value.EnableFinalModelNormalizationPass)
            {
                var findingsBeforeNormalization = findings.ToArray();
                await PersistAndPublishAsync(
                    run,
                    "Финальная нормализация findings через модель",
                    ReviewPipelineStage.FindingsNormalization,
                    76,
                    cancellationToken);
                var normalized = await RunFinalModelNormalizationAsync(
                    findings,
                    opportunities,
                    cancellationToken);
                findings = RestoreDroppedDistinctFindings(findingsBeforeNormalization, normalized.Findings).ToList();
                opportunities = normalized.Opportunities.ToList();
                logger.LogInformation(
                    "Final model normalization for run {RunId}: findings={Findings}, opportunities={Opportunities}, rawFindings={RawFindings}, restoredFindings={RestoredFindings}",
                    run.Id,
                    findings.Count,
                    opportunities.Count,
                    findingsBeforeNormalization.Length,
                    findings.Count - normalized.Findings.Count);
            }

            var primaryOpportunities = opportunities;
            var findingsComparison = previousRun is null
                ? null
                : HasNoChangesSincePreviousReview(previousRun, preprocessed)
                    ? findingsComparisonService.CompareUnchangedDiff(
                        previousRun.Id,
                        previousRun.Findings,
                        findings)
                    : findingsComparisonService.Compare(
                        previousRun.Id,
                        previousRun.Findings,
                        findings);
            run.UpdateFindings(findings);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, $"Collected {findings.Count} findings from chunk responses", ReviewPipelineStage.FinalSynthesis, 87, cancellationToken);

            var inlineComments = markdownReportBuilder.BuildInlineComments(findings, preprocessed.FilteredDiffText);
            var reviewedFiles = await BuildReviewedFilesAsync(diffResult, inlineComments, cancellationToken);
            var enrichedInlineComments = FlattenInlineThreads(reviewedFiles);
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                PreparedChunks = preprocessed.Chunks,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDescriptionStructured = changeSummary.StructuredContent,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                InlineComments = enrichedInlineComments,
                ReviewedFiles = reviewedFiles,
                PrimaryOpportunities = primaryOpportunities,
                FindingsComparison = findingsComparison
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "File review workspace generated", ReviewPipelineStage.FinalSynthesis, 90, cancellationToken);

            var fullReport = markdownReportBuilder.BuildFullReport(run.DisplayTitle, description, findings, findingsComparison);
            var summaryComment = markdownReportBuilder.BuildSummaryComment(run.DisplayTitle, description, findings, findingsComparison);
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                PreparedChunks = preprocessed.Chunks,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDescriptionStructured = changeSummary.StructuredContent,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                MarkdownReport = fullReport,
                SummaryComment = summaryComment,
                InlineComments = enrichedInlineComments,
                ReviewedFiles = reviewedFiles,
                PrimaryOpportunities = primaryOpportunities,
                FindingsComparison = findingsComparison
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Final report generated", ReviewPipelineStage.FinalSynthesis, 93, cancellationToken);

            var publishSucceeded = false;
            if (request.PublishMode != PublishMode.None &&
                request.TargetKind == ReviewTargetKind.PullRequest &&
                !string.IsNullOrWhiteSpace(request.PullRequestAccessToken) &&
                !string.IsNullOrWhiteSpace(request.Target.PullRequestUrl))
            {
                await PersistAndPublishAsync(run, "Publishing review comments", ReviewPipelineStage.Publish, 95, cancellationToken);
                publishSucceeded = await reviewPublisher.PublishAsync(
                    request.Target.PullRequestUrl,
                    request.PullRequestAccessToken,
                    request.PublishMode,
                    summaryComment,
                    enrichedInlineComments,
                    cancellationToken);
            }

            var artifacts = new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                PreparedChunks = preprocessed.Chunks,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = description,
                ChangeDescriptionStructured = changeSummary.StructuredContent,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                MarkdownReport = fullReport,
                SummaryComment = summaryComment,
                InlineComments = enrichedInlineComments,
                ReviewedFiles = reviewedFiles,
                PrimaryOpportunities = primaryOpportunities,
                FindingsComparison = findingsComparison
            };

            run.Complete(artifacts, findings, publishSucceeded);
            var completedUpdate = new ReviewProgressUpdate(
                run.Id,
                run.Status,
                run.CurrentStage,
                run.ProgressPercent,
                run.CurrentMessage,
                DateTimeOffset.UtcNow,
                true);
            run.RecordProgress(completedUpdate);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await TryIndexSemanticArtifactsAsync(run, cancellationToken);
            await reviewProgressStore.PublishAsync(completedUpdate, cancellationToken);
            reviewProgressStore.Complete(run.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Review run {RunId} was cancelled", runId);
            run.Cancel("Ревью остановлено пользователем.");
            var cancelledUpdate = new ReviewProgressUpdate(
                run.Id,
                run.Status,
                run.CurrentStage,
                run.ProgressPercent,
                run.CurrentMessage,
                DateTimeOffset.UtcNow,
                true);
            run.RecordProgress(cancelledUpdate);
            await reviewRunRepository.UpdateAsync(run, CancellationToken.None);
            await reviewProgressStore.PublishAsync(cancelledUpdate, CancellationToken.None);
            reviewProgressStore.Complete(run.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Review run {RunId} failed", runId);
            run.Fail(exception.Message);
            var failedUpdate = new ReviewProgressUpdate(
                run.Id,
                run.Status,
                run.CurrentStage,
                run.ProgressPercent,
                exception.Message,
                DateTimeOffset.UtcNow,
                true);
            run.RecordProgress(failedUpdate);
            await reviewRunRepository.UpdateAsync(run, CancellationToken.None);
            await reviewProgressStore.PublishAsync(failedUpdate, CancellationToken.None);
            reviewProgressStore.Complete(run.Id);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(diffResult?.CleanupDirectory) &&
                Directory.Exists(diffResult.CleanupDirectory))
            {
                try
                {
                    Directory.Delete(diffResult.CleanupDirectory, true);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(cleanupException, "Could not delete temporary review workspace {Directory}", diffResult.CleanupDirectory);
                }
            }

            if (!string.IsNullOrWhiteSpace(roslynCleanupDirectory) &&
                Directory.Exists(roslynCleanupDirectory))
            {
                try
                {
                    Directory.Delete(roslynCleanupDirectory, true);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(cleanupException, "Could not delete Roslyn workspace {Directory}", roslynCleanupDirectory);
                }
            }
        }
    }

    private async Task TryIndexSemanticArtifactsAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        try
        {
            await reviewSemanticIndex.IndexPreparedChunksAsync(run, cancellationToken);
            await reviewSemanticIndex.IndexFindingsAsync(run, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to index semantic review artifacts for run {RunId}", run.Id);
        }
    }

    private async Task<ReviewRun?> ResolveBaselineRunAsync(
        ReviewExecutionRequest request,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        if (request.BaselineRunId is null)
        {
            return await reviewRunRepository.FindLatestCompletedForTargetAsync(run.Target, run.CreatedAt, cancellationToken);
        }

        var baselineRun = await reviewRunRepository.GetAsync(request.BaselineRunId.Value, cancellationToken)
                          ?? throw new InvalidOperationException($"Baseline review run '{request.BaselineRunId}' was not found.");
        if (baselineRun.Status != ReviewRunStatus.Completed)
        {
            throw new InvalidOperationException("Baseline review run must be completed.");
        }

        if (!IsSameReviewTarget(run.Target, baselineRun.Target))
        {
            throw new InvalidOperationException("Baseline review run belongs to another target.");
        }

        return baselineRun;
    }

    private static bool IsSameReviewTarget(ReviewTargetDescriptor left, ReviewTargetDescriptor right)
    {
        return left.Kind == right.Kind &&
               string.Equals(left.PullRequestUrl, right.PullRequestUrl, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RepositoryPath, right.RepositoryPath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RepositoryName, right.RepositoryName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.SourceBranch, right.SourceBranch, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.TargetBranch, right.TargetBranch, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DiffAcquisitionResult> AcquireDiffAsync(
        ReviewExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return request.TargetKind switch
        {
            ReviewTargetKind.PullRequest when !string.IsNullOrWhiteSpace(request.Target.PullRequestUrl) =>
                await pullRequestDiffService.GetDiffAsync(
                    request.Target.PullRequestUrl,
                    request.PullRequestAccessToken,
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

    private void LogReviewPipelineConfiguration(ReviewRun run, ReviewExecutionRequest request)
    {
        var o = reviewPipelineOptions.Value;
        logger.LogInformation(
            "Review pipeline configuration for run {RunId}: ProviderProfileId={ProviderProfileId}, ForceRerun={ForceRerun}, " +
            "MaxChunkCharacters={MaxChunkCharacters}, MaxPrimaryReviewChunkCharacters={MaxPrimaryReviewChunkCharacters}, " +
            "MergePrimaryReviewChunks={MergePrimaryReviewChunks}, MaxConcurrentChunkReviews={MaxConcurrentChunkReviews}, " +
            "MaxChangeSummaryCharacters={MaxChangeSummaryCharacters}, RoslynEnabled={RoslynEnabled}, " +
            "SinglePassFullDiffAndGraphPrimaryReview={SinglePassPrimary}, SinglePassFullContextMaxCharacters={SinglePassMax}, " +
            "IncludeRoslynGraphInFullContextPayload={IncludeGraph}, FullContextMaxToolIterations={FullContextMaxToolIterations}, " +
            "EnableFinalModelNormalizationPass={EnableFinalNormalization}",
            run.Id,
            string.IsNullOrWhiteSpace(request.ProviderProfileId) ? "(routing default)" : request.ProviderProfileId,
            request.ForceRerun,
            o.MaxChunkCharacters,
            o.MaxPrimaryReviewChunkCharacters,
            o.MergePrimaryReviewChunks,
            o.MaxConcurrentChunkReviews,
            o.MaxChangeSummaryCharacters,
            o.Roslyn.Enabled,
            o.SinglePassFullDiffAndGraphPrimaryReview,
            o.SinglePassFullContextMaxCharacters,
            o.IncludeRoslynGraphInFullContextPayload,
            o.FullContextMaxToolIterations,
            o.EnableFinalModelNormalizationPass);
    }

    private async Task<ChangeSummaryResult> GenerateChangeSummaryAsync(
        ReviewRun run,
        string reviewTitle,
        PreprocessedDiff preprocessed,
        ReviewExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChangeDescription,
            request.ProviderProfileId,
            request.StageOverrides,
            cancellationToken);

        var fileList = string.Join('\n', preprocessed.ChangedFiles.Take(100));
        var maxChangeSummaryCharacters = Math.Max(4000, reviewPipelineOptions.Value.MaxChangeSummaryCharacters);
        var fullDiffLength = preprocessed.ReviewContextDiffText.Length;
        var diffSnippet = fullDiffLength > maxChangeSummaryCharacters
            ? preprocessed.ReviewContextDiffText[..maxChangeSummaryCharacters]
            : preprocessed.ReviewContextDiffText;
        if (fullDiffLength > maxChangeSummaryCharacters)
        {
            logger.LogInformation(
                "Change description diff snippet truncated for run {RunId}: fullDiffLength={FullDiffLength}, maxChangeSummaryCharacters={MaxChangeSummaryCharacters}, truncatedCharacterCount={TruncatedCount}",
                run.Id,
                fullDiffLength,
                maxChangeSummaryCharacters,
                fullDiffLength - maxChangeSummaryCharacters);
        }

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
            DiagramMermaid = diagram,
            StructuredContent = parsed.StructuredContent
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
                ? ParseChangeDescription(descriptionNode)
                : null;
            var diagram = root.TryGetProperty("diagram", out var diagramNode)
                ? diagramNode.GetString()
                : null;

            if (description is not null && !string.IsNullOrWhiteSpace(description.Description))
            {
                return new ChangeSummaryResult
                {
                    Description = description.Description,
                    DiagramMermaid = MermaidDiagramNormalizer.Normalize(diagram),
                    StructuredContent = description.StructuredContent
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

    private static ParsedChangeDescription? ParseChangeDescription(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            var value = node.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return new ParsedChangeDescription(value.Trim(), null);
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var category = node.TryGetProperty("category", out var categoryNode)
            ? categoryNode.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var estimatedReviewEffort = TryParseBoundedInt(node, "estimated_review_effort", 1, 5);
        var qualityScore = TryParseBoundedInt(node, "quality_score", 0, 100);
        var summary = node.TryGetProperty("summary", out var summaryNode)
            ? summaryNode.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var impactedModules = node.TryGetProperty("impacted_modules", out var impactedModulesNode)
            ? ParseStringArray(impactedModulesNode)
            : [];
        var risks = node.TryGetProperty("risks", out var risksNode)
            ? ParseStringArray(risksNode)
            : [];

        var description = RenderChangeDescriptionMarkdown(
            category,
            estimatedReviewEffort,
            qualityScore,
            summary,
            impactedModules,
            risks);
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        return new ParsedChangeDescription(
            description,
            new ChangeDescriptionStructuredContent
            {
                Category = category,
                EstimatedReviewEffort = estimatedReviewEffort,
                QualityScore = qualityScore,
                Summary = summary,
                ImpactedModules = impactedModules,
                Risks = risks
            });
    }

    private static int? TryParseBoundedInt(JsonElement node, string propertyName, int min, int max)
    {
        if (!node.TryGetProperty(propertyName, out var propertyNode))
        {
            return null;
        }

        int? parsed = propertyNode.ValueKind switch
        {
            JsonValueKind.Number when propertyNode.TryGetInt32(out var numericValue) => numericValue,
            JsonValueKind.String when int.TryParse(propertyNode.GetString(), out var stringValue) => stringValue,
            _ => null
        };

        if (parsed is null)
        {
            return null;
        }

        return parsed.Value >= min && parsed.Value <= max
            ? parsed.Value
            : null;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return node
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static string RenderChangeDescriptionMarkdown(
        string category,
        int? estimatedReviewEffort,
        int? qualityScore,
        string summary,
        IReadOnlyList<string> impactedModules,
        IReadOnlyList<string> risks)
    {
        var builder = new StringBuilder();

        if (estimatedReviewEffort is not null)
        {
            builder.AppendLine($"**Сложность ревью:** {estimatedReviewEffort}/5");
            builder.AppendLine();
        }

        if (qualityScore is not null)
        {
            builder.AppendLine($"**Оценка качества PR:** {qualityScore}/100");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            builder.AppendLine($"**Категория:** {category}");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.AppendLine(summary);
            builder.AppendLine();
        }

        if (impactedModules.Count > 0)
        {
            builder.AppendLine("**Затронутые модули:**");
            foreach (var module in impactedModules)
            {
                builder.AppendLine($"- {module}");
            }

            builder.AppendLine();
        }

        if (risks.Count > 0)
        {
            builder.AppendLine("**Риски и точки внимания:**");
            foreach (var risk in risks)
            {
                builder.AppendLine($"- {risk}");
            }
        }

        return builder.ToString().Trim();
    }

    private async Task<string?> GenerateChangeDiagramAsync(
        ProviderProfile profile,
        string model,
        double temperature,
        PreprocessedDiff preprocessed,
        string description,
        CancellationToken cancellationToken)
    {
        var fileList = string.Join('\n', preprocessed.ChangedFiles.Take(40));

        var response = await llmCompletionService.CompleteAsync(
            profile,
            new LlmChatRequest
            {
                Model = model,
                Temperature = temperature,
                SystemPrompt = """
                    You are a Lead System Architect.
                    Produce only Mermaid code for a concise high-level semantic change diagram.
                    Prefer "flowchart LR".
                    Show the changed capability as an architecture-level flow, not as a full class-by-class call graph.
                    Prefer modules, layers, services, bounded contexts, and external systems over concrete classes.
                    Keep the diagram easy to read: 4-8 nodes and no more than 8 edges.
                    Merge repeated implementation details into a single node per subsystem.
                    Do not include DTOs, validators, AutoMapper profiles, configuration classes, test classes, or utility helpers unless they are the main point of the change.
                    Include cache, queue, or database only if they materially explain the changed behavior.
                    If several endpoints share the same path, represent them as one API node.
                    Use short labels with business or architectural meaning.
                    Always declare nodes as ID["Label text"] first, then connect them.
                    Return an empty response if a diagram is not useful.
                    """,
                UserPrompt =
                    $"Description:\n{description}\n\nChanged files:\n{fileList}"
            },
            cancellationToken);

        return MermaidDiagramNormalizer.Normalize(response);
    }

    private async Task<IReadOnlyList<string>> ReviewSinglePassFullContextAsync(
        string description,
        PreprocessedDiff preprocessed,
        DiffAcquisitionResult diffResult,
        ReviewExecutionRequest request,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var deterministicContext = await LoadDeterministicReviewContextAsync(
            preprocessed,
            diffResult,
            run,
            cancellationToken);
        var payload = BuildSinglePassDiffAndGraphPayload(preprocessed, deterministicContext);
        logger.LogInformation(
            "Full-context primary review for run {RunId}: payloadChars={PayloadChars}, reviewHints={ReviewHints}, deterministicToolResponses={ToolResponses}",
            run.Id,
            payload.Length,
            preprocessed.ReviewHints.Count,
            deterministicContext.Count);

        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            request.ProviderProfileId,
            request.StageOverrides,
            cancellationToken);

        await PersistAndPublishAsync(
            run,
            "Первичное ревью: полный diff + graph + tool-loop",
            ReviewPipelineStage.ChunkReview,
            52,
            cancellationToken);

        var systemPrompt = reviewPromptFactory.BuildSinglePassPrimaryReviewSystemPrompt(description);
        var userPrompt = reviewPromptFactory.BuildSinglePassPrimaryReviewUserPrompt(payload);
        var maxIterations = Math.Clamp(reviewPipelineOptions.Value.FullContextMaxToolIterations, 1, 6);

        var response = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = selection.Temperature,
                ExpectJson = true,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt
            },
            cancellationToken);

        var currentPayload = userPrompt;
        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var envelope = ParseChunkReviewAgentEnvelope(response);
            logger.LogInformation(
                "Full-context tool decision for run {RunId}, iteration {Iteration}: NeedMoreContext={NeedMoreContext}, ToolRequestsCount={ToolRequestsCount}",
                run.Id,
                iteration,
                envelope.NeedMoreContext,
                envelope.ToolRequests.Count);
            if (!envelope.NeedMoreContext || envelope.ToolRequests.Count == 0)
            {
                break;
            }

            var toolResponses = await reviewWorkspaceToolExecutor.ExecuteAsync(
                diffResult,
                "(full-diff)",
                envelope.ToolRequests,
                cancellationToken);
            if (toolResponses.Count == 0)
            {
                logger.LogInformation(
                    "Full-context tool loop produced no results for run {RunId}, iteration {Iteration}",
                    run.Id,
                    iteration);
                break;
            }

            currentPayload = BuildChunkReviewPayload(currentPayload, toolResponses);
            response = await llmCompletionService.CompleteAsync(
                selection.Profile,
                new LlmChatRequest
                {
                    Model = selection.Model,
                    Temperature = selection.Temperature,
                    ExpectJson = true,
                    SystemPrompt = reviewPromptFactory.BuildChunkReviewSystemPrompt(
                        description,
                        ReviewPromptSpecialRules.PrimaryReviewFinalizationRules),
                    UserPrompt = reviewPromptFactory.BuildUserPrompt(
                        ReviewPipelineStage.ChunkReview,
                        currentPayload)
                },
                cancellationToken);
        }

        var coverageCriticResponse = await RunDeterministicCoverageCriticAsync(
            description,
            preprocessed,
            deterministicContext,
            response,
            selection,
            run,
            cancellationToken);

        await PersistAndPublishAsync(
            run,
            "Первичное ревью (full-context) завершено",
            ReviewPipelineStage.ChunkReview,
            70,
            cancellationToken);

        return string.IsNullOrWhiteSpace(coverageCriticResponse)
            ? [response]
            : [response, coverageCriticResponse];
    }

    private async Task<string?> RunDeterministicCoverageCriticAsync(
        string description,
        PreprocessedDiff preprocessed,
        IReadOnlyList<ReviewWorkspaceToolResponse> deterministicContext,
        string primaryReviewResponse,
        StageRouteSelection selection,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        if (preprocessed.ReviewHints.Count == 0 || deterministicContext.Count == 0)
        {
            return null;
        }

        await PersistAndPublishAsync(
            run,
            "Проверяем покрытие deterministic review-hints",
            ReviewPipelineStage.ChunkReview,
            68,
            cancellationToken);

        var hintsBlock = ReviewHintFormatter.BuildAllHintsBlock(preprocessed.ReviewHints);
        var contextBlock = BuildDeterministicContextBlock(deterministicContext);
        var response = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = 0,
                ExpectJson = true,
                SystemPrompt = reviewPromptFactory.BuildDeterministicCoverageCriticSystemPrompt(description),
                UserPrompt = reviewPromptFactory.BuildDeterministicCoverageCriticUserPrompt(
                    hintsBlock,
                    contextBlock,
                    primaryReviewResponse)
            },
            cancellationToken);

        logger.LogInformation(
            "Deterministic coverage critic completed for run {RunId}: responseChars={ResponseChars}",
            run.Id,
            response.Length);

        return response;
    }

    private async Task<IReadOnlyList<ReviewWorkspaceToolResponse>> LoadDeterministicReviewContextAsync(
        PreprocessedDiff preprocessed,
        DiffAcquisitionResult diffResult,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var requests = BuildDeterministicToolRequests(preprocessed);
        if (requests.Count == 0)
        {
            return [];
        }

        await PersistAndPublishAsync(
            run,
            $"Подбираем deterministic context для {requests.Count} review-hints",
            ReviewPipelineStage.ChunkReview,
            50,
            cancellationToken);

        var responses = await reviewWorkspaceToolExecutor.ExecuteAsync(
            diffResult,
            "(deterministic-prefetch)",
            requests,
            cancellationToken);

        logger.LogInformation(
            "Deterministic review context for run {RunId}: Requests={RequestCount}, Responses={ResponseCount}",
            run.Id,
            requests.Count,
            responses.Count);

        return responses;
    }

    private static IReadOnlyList<ReviewWorkspaceToolRequest> BuildDeterministicToolRequests(PreprocessedDiff preprocessed)
    {
        if (preprocessed.ReviewHints.Count == 0)
        {
            return [];
        }

        var requests = new List<ReviewWorkspaceToolRequest>();

        foreach (var hint in preprocessed.ReviewHints.OrderBy(GetDeterministicHintPriority))
        {
            if (hint.Category.StartsWith("SQL/", StringComparison.OrdinalIgnoreCase))
            {
                requests.Add(new ReviewWorkspaceToolRequest(
                    "read_file",
                    "Read the complete SQL query to verify join usage, predicates, and cardinality.",
                    FilePath: hint.FilePath,
                    StartLine: 1,
                    MaxLines: 220));

                var useCaseName = Path.GetFileNameWithoutExtension(hint.FilePath);
                if (!string.IsNullOrWhiteSpace(useCaseName))
                {
                    requests.Add(new ReviewWorkspaceToolRequest(
                        "find_usage",
                        "Find production consumers of the changed SQL use-case.",
                        Query: useCaseName,
                        MaxLines: 120));
                }

                continue;
            }

            if (hint.Category.StartsWith("Configuration", StringComparison.OrdinalIgnoreCase))
            {
                requests.Add(new ReviewWorkspaceToolRequest(
                    "grep_code",
                    "Find options binding and nearby configuration section usage.",
                    Query: "Configure<",
                    PathScope: "src",
                    MaxLines: 120));

                requests.AddRange(preprocessed.ChangedFiles
                    .Where(file => Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                                   file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(file => new ReviewWorkspaceToolRequest(
                        "read_file",
                        "Read changed appsettings file to verify option section shape.",
                        FilePath: file,
                        StartLine: 1,
                        MaxLines: 260)));

                continue;
            }

            if (hint.Category.StartsWith("RuntimeFlow/", StringComparison.OrdinalIgnoreCase))
            {
                requests.Add(new ReviewWorkspaceToolRequest(
                    "read_file",
                    "Read the runtime flow around the deterministic Kafka/cache/polling hint.",
                    FilePath: hint.FilePath,
                    StartLine: Math.Max(1, hint.StartLine - 40),
                    MaxLines: 220));

                foreach (var query in ExtractRuntimeFlowQueries(hint))
                {
                    requests.Add(new ReviewWorkspaceToolRequest(
                        "grep_code",
                        "Find related runtime flow entrypoints and side effects.",
                        Query: query,
                        MaxLines: 140));
                }

                continue;
            }

            if (hint.Category.StartsWith("CDC/", StringComparison.OrdinalIgnoreCase))
            {
                requests.Add(new ReviewWorkspaceToolRequest(
                    "read_file",
                    "Read the CDC registration or EF relationship around the deterministic CDC hint.",
                    FilePath: hint.FilePath,
                    StartLine: 1,
                    MaxLines: 220));

                var entityName = ExtractCdcEntityName(hint);
                if (!string.IsNullOrWhiteSpace(entityName))
                {
                    requests.Add(new ReviewWorkspaceToolRequest(
                        "find_usage",
                        "Find usages and write paths for the CDC entity.",
                        Query: entityName,
                        MaxLines: 160));

                    var pluralEntityName = PluralizeEntityName(entityName);
                    if (!string.Equals(pluralEntityName, entityName, StringComparison.Ordinal))
                    {
                        requests.Add(new ReviewWorkspaceToolRequest(
                            "grep_code",
                            "Find DbSet/table usages for the CDC entity, including local writes.",
                            Query: pluralEntityName,
                            MaxLines: 160));
                    }
                }

                requests.Add(new ReviewWorkspaceToolRequest(
                    "grep_code",
                    "Find all CDC registrations in the service to reason about topic ownership and ordering.",
                    Query: "AddCdcConsumer",
                    MaxLines: 160));

                continue;
            }

            if (hint.Category.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase))
            {
                var seedName = ExtractSeedName(hint.Evidence);
                if (!string.IsNullOrWhiteSpace(seedName))
                {
                    requests.Add(new ReviewWorkspaceToolRequest(
                        "read_file",
                        "Read seed helper referenced by the changed test.",
                        Query: seedName,
                        PathScope: "Test",
                        StartLine: 1,
                        MaxLines: 220));

                    requests.Add(new ReviewWorkspaceToolRequest(
                        "find_usage",
                        "Find all usages of the seed helper referenced by the changed test.",
                        Query: seedName,
                        PathScope: "Test",
                        MaxLines: 120));
                }

                continue;
            }

            if (hint.RuleId is "GROUP_BY_FIRST_WITHOUT_ORDER" or "NON_NULLABLE_CONTRACT_RETURNS_NULL")
            {
                requests.Add(new ReviewWorkspaceToolRequest(
                    "read_file",
                    "Read the full file around the hinted contract/data-integrity risk.",
                    FilePath: hint.FilePath,
                    StartLine: Math.Max(1, hint.StartLine - 40),
                    MaxLines: 180));
            }
        }

        return requests
            .Where(request =>
                !string.IsNullOrWhiteSpace(request.Query) ||
                !string.IsNullOrWhiteSpace(request.FilePath))
            .DistinctBy(request => $"{request.ToolName}|{request.Query}|{request.FilePath}|{request.PathScope}|{request.StartLine}|{request.MaxLines}")
            .Take(24)
            .ToArray();
    }

    private static int GetDeterministicHintPriority(ReviewHint hint)
    {
        if (hint.Category.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (hint.Category.StartsWith("Configuration", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(hint.RuleId, "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING", StringComparison.Ordinal))
        {
            return 2;
        }

        if (hint.Category.StartsWith("RuntimeFlow/", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (hint.Category.StartsWith("CDC/", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (hint.Category.StartsWith("SQL/", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        return hint.RuleId switch
        {
            "GROUP_BY_FIRST_WITHOUT_ORDER" => 6,
            "NON_NULLABLE_CONTRACT_RETURNS_NULL" => 7,
            _ => 6
        };
    }

    private static string ExtractSeedName(string evidence)
    {
        var match = Regex.Match(evidence, @"\b(Seed[A-Za-z0-9_]+)\.", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static IReadOnlyList<string> ExtractRuntimeFlowQueries(ReviewHint hint)
    {
        return hint.RuleId switch
        {
            "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING" =>
            [
                "HandleMarkerAsync",
                "SaveHandlingMarkerAsync",
                "PublishAsync",
                "RedisPubSub",
                "ClientCategoryIds",
                "GetHandlingClientCategory",
                "GetHandlingMarkersAsync"
            ],
            "RUNTIME_FAIL_OPEN_CONTEXT_FILTER" =>
            [
                "GetHandlingClientCategory",
                "ClientCategoryIds",
                "return null"
            ],
            "RUNTIME_POLLING_OFFSET_CONTEXT" =>
            [
                "GetSessionOffsetAsync",
                "SaveSessionOffsetAsync",
                "SessionId"
            ],
            "RUNTIME_STREAM_POLLING_PARITY" =>
            [
                "StreamMarkersAsync",
                "ServerSentEvents",
                "PublishAsync",
                "GetHandlingMarkersAsync"
            ],
            _ =>
            [
                "SaveHandlingMarkerAsync",
                "GetSessionOffsetAsync",
                "ClientCategory"
            ]
        };
    }

    private static string ExtractCdcEntityName(ReviewHint hint)
    {
        var messageMatch = Regex.Match(hint.Message, @"entity '([^']+)'", RegexOptions.CultureInvariant);
        if (messageMatch.Success)
        {
            return messageMatch.Groups[1].Value;
        }

        var relationMatch = Regex.Match(
            hint.Message,
            @"CDC entities '([^']+)' and '([^']+)'",
            RegexOptions.CultureInvariant);
        if (relationMatch.Success)
        {
            return relationMatch.Groups[1].Value;
        }

        var evidenceMatch = Regex.Match(
            hint.Evidence,
            @"AddCdcConsumer\s*<\s*[A-Za-z_][A-Za-z0-9_.]*\s*,\s*(?<entity>[A-Za-z_][A-Za-z0-9_.]*)\s*>",
            RegexOptions.CultureInvariant);
        if (!evidenceMatch.Success)
        {
            return string.Empty;
        }

        var rawEntity = evidenceMatch.Groups["entity"].Value;
        var dotIndex = rawEntity.LastIndexOf('.');
        return dotIndex >= 0 ? rawEntity[(dotIndex + 1)..] : rawEntity;
    }

    private static string PluralizeEntityName(string entityName)
    {
        if (entityName.EndsWith("y", StringComparison.Ordinal) &&
            entityName.Length > 1 &&
            "aeiou".IndexOf(char.ToLowerInvariant(entityName[^2]), StringComparison.Ordinal) < 0)
        {
            return entityName[..^1] + "ies";
        }

        return entityName.EndsWith("s", StringComparison.Ordinal)
            ? entityName
            : entityName + "s";
    }

    private string BuildSinglePassDiffAndGraphPayload(
        PreprocessedDiff preprocessed,
        IReadOnlyList<ReviewWorkspaceToolResponse> deterministicContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== DIFF ===");
        sb.AppendLine(preprocessed.FilteredDiffText.Trim());
        sb.AppendLine();
        var hintsBlock = ReviewHintFormatter.BuildAllHintsBlock(preprocessed.ReviewHints);
        if (!string.IsNullOrWhiteSpace(hintsBlock))
        {
            sb.AppendLine(hintsBlock);
            sb.AppendLine();
        }

        var deterministicContextBlock = BuildDeterministicContextBlock(deterministicContext);
        if (!string.IsNullOrWhiteSpace(deterministicContextBlock))
        {
            sb.AppendLine(deterministicContextBlock);
            sb.AppendLine();
        }

        if (!reviewPipelineOptions.Value.IncludeRoslynGraphInFullContextPayload)
        {
            sb.AppendLine("=== ROSLYN GRAPH ===");
            sb.AppendLine("(explicitly disabled by IncludeRoslynGraphInFullContextPayload=false)");
        }
        else if (preprocessed.Graph is { Nodes.Count: > 0 } graph)
        {
            sb.AppendLine("=== ROSLYN GRAPH (JSON; same shape as graph.json) ===");
            sb.AppendLine(graph.ToJsonString());
        }
        else
        {
            sb.AppendLine("=== ROSLYN GRAPH ===");
            sb.AppendLine("(граф недоступен — Roslyn выключен или граф пуст)");
        }

        var combined = sb.ToString();
        var max = reviewPipelineOptions.Value.SinglePassFullContextMaxCharacters;
        if (max > 0 && combined.Length > max)
        {
            combined = combined[..max] + "\n… [truncated by SinglePassFullContextMaxCharacters]";
        }

        return combined;
    }

    private static string BuildDeterministicContextBlock(IReadOnlyList<ReviewWorkspaceToolResponse> deterministicContext)
    {
        if (deterministicContext.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("=== DETERMINISTIC SUPPLEMENTAL CONTEXT (supporting evidence, not changed code) ===");
        foreach (var response in deterministicContext)
        {
            builder.AppendLine($"Tool: {response.ToolName}");
            builder.AppendLine($"Source: {response.Source}");
            if (!string.IsNullOrWhiteSpace(response.FilePath))
            {
                builder.AppendLine($"File: {response.FilePath}");
            }

            if (response.StartLine > 0)
            {
                builder.AppendLine($"Lines: {response.StartLine}-{response.EndLine}");
            }

            builder.AppendLine("Content:");
            builder.AppendLine(TrimForPrompt(response.Content, 12000));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<ReviewFinding> BuildConfirmedDeterministicFindings(
        IReadOnlyList<ReviewHint> hints,
        IReadOnlyList<ReviewFinding> existingFindings)
    {
        return hints
            .Select(BuildConfirmedDeterministicFinding)
            .Where(finding => finding is not null)
            .Cast<ReviewFinding>()
            .Where(finding => existingFindings.All(existing => !CoversFinding(existing, finding)))
            .ToArray();
    }

    private static ReviewFinding? BuildConfirmedDeterministicFinding(ReviewHint hint)
    {
        return hint.RuleId switch
        {
            "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING" => BuildKafkaCachePublishFinding(hint),
            "API_UNBOUNDED_PAGE_SIZE" => BuildUnboundedPageSizeFinding(hint),
            "EF_BULK_UPDATE_THEN_INSERT_WITHOUT_TRANSACTION" => BuildNonAtomicBulkUpdateFinding(hint),
            "CONFIG_SECRET_LIKE_VALUE" => BuildSecretLikeConfigFinding(hint),
            _ => null
        };
    }

    private static ReviewFinding? BuildRoslynBootstrapFailureFinding(RoslynWorkspaceBootstrapResult bootstrap)
    {
        if (bootstrap.Success || string.IsNullOrWhiteSpace(bootstrap.ErrorMessage))
        {
            return null;
        }

        var error = bootstrap.ErrorMessage;
        if (!IsRestoreOrBuildFailure(error))
        {
            return null;
        }

        var file = BuildBootstrapFindingFile(bootstrap);
        var isTimeout = error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        var title = isTimeout
            ? "dotnet restore не завершился в отведённое время"
            : "dotnet restore не проходит для source-ветки";
        var description = isTimeout
            ? "Roslyn/bootstrap не смог подготовить полноценный workspace: `dotnet restore` завис до timeout. Такой PR нельзя считать проверенным как buildable, а review потерял Roslyn-граф и часть cross-file контекста."
            : "Roslyn/bootstrap не смог подготовить полноценный workspace: `dotnet restore` завершился ошибкой. Такой PR нельзя считать готовым к merge, пока source-ветка не восстанавливается из доступных package feeds; кроме того, review потерял Roslyn-граф и часть cross-file контекста.";

        return new ReviewFinding(
            file,
            "dotnet restore",
            FindingCategory.Reliability,
            FindingSeverity.Critical,
            ReviewFindingSource.InitialReview,
            title,
            description,
            TrimForPrompt(error, 2500),
            "Починить restore/build source-ветки: проверить версии внутренних NuGet-пакетов, доступность feed и `global.json`/SDK. После исправления перезапустить ревью, чтобы Roslyn graph построился на полноценном workspace.",
            1,
            1);
    }

    private static bool IsRestoreOrBuildFailure(string message)
    {
        return message.Contains("dotnet restore failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("dotnet restore timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("dotnet build failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("NU110", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Restore", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildBootstrapFindingFile(RoslynWorkspaceBootstrapResult bootstrap)
    {
        if (!string.IsNullOrWhiteSpace(bootstrap.WorkspaceDirectory) &&
            !string.IsNullOrWhiteSpace(bootstrap.SolutionPath))
        {
            try
            {
                return Path.GetRelativePath(bootstrap.WorkspaceDirectory, bootstrap.SolutionPath)
                    .Replace('\\', '/');
            }
            catch
            {
                return Path.GetFileName(bootstrap.SolutionPath);
            }
        }

        return !string.IsNullOrWhiteSpace(bootstrap.SolutionPath)
            ? Path.GetFileName(bootstrap.SolutionPath)
            : "restore/build";
    }

    private static ReviewFinding BuildKafkaCachePublishFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : string.Empty,
            FindingCategory.Security,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Kafka-handler сохраняет и публикует маркер до проверки видимости",
            "В обработчике Kafka маркер сохраняется в cache/pubsub до того, как видимые в модели ограничения по системе, клиентской категории, зоне или похожему бизнес-контексту применены к данным. Если этот cache/pubsub затем читается SSE или другим пользовательским каналом, пользователь может получить маркер, который polling/list endpoint позже отфильтровал бы.",
            hint.Evidence,
            "Перенести проверку бизнес-видимости до SaveHandlingMarkerAsync/PublishAsync либо не публиковать пользовательски наблюдаемый payload до контекстной фильтрации. Если cache должен оставаться raw, фильтр должен быть гарантирован на каждом выходном канале до отправки пользователю.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildUnboundedPageSizeFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "PageSize",
            FindingCategory.Performance,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "PageSize валидируется только как положительное число",
            "В запросе списка появился `PageSize`, но deterministic scan видит только нижнюю границу (`> 0`) и не видит максимального лимита. Пользователь может запросить очень большую страницу, что создаёт нагрузку на БД и память приложения.",
            hint.Evidence,
            "Добавить верхний лимит `PageSize` в валидатор/модель запроса и покрыть его тестом. Лимит лучше держать константой или options, согласованной с контрактом сервиса.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildNonAtomicBulkUpdateFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "ExecuteUpdateAsync",
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Bulk update и последующая вставка выполняются без общей транзакции",
            "В изменённом методе есть `ExecuteUpdateAsync`, после которого видна вставка/`SaveChangesAsync`, но не видна явная транзакция. Если вторая операция упадёт после успешного bulk update, данные могут остаться в промежуточном состоянии.",
            hint.Evidence,
            "Обернуть связанные bulk update и insert/save в одну транзакцию или изменить алгоритм так, чтобы операция была атомарной и идемпотентно восстанавливалась после частичного сбоя.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildSecretLikeConfigFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "secret-like config",
            FindingCategory.Security,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "В конфиг добавлено секретоподобное значение",
            "В изменённом `appsettings*.json` видно непустое значение, похожее на пароль, signing key, token или другой секрет. Даже для test/dev окружений такие значения стоит проверять: они могут быть переиспользуемыми и попасть в историю репозитория.",
            hint.Evidence,
            "Вынести секреты в защищённое хранилище/переменные окружения или заменить явным безопасным placeholder. Если значение намеренно тестовое, зафиксировать это в конфигурационных правилах сервиса.",
            hint.StartLine,
            hint.StartLine);
    }

    private static IReadOnlyList<ReviewFinding> RestoreDroppedDistinctFindings(
        IReadOnlyList<ReviewFinding> originalFindings,
        IReadOnlyList<ReviewFinding> normalizedFindings)
    {
        if (originalFindings.Count == 0)
        {
            return normalizedFindings;
        }

        var restored = normalizedFindings.ToList();
        foreach (var candidate in originalFindings)
        {
            if (candidate.Severity is FindingSeverity.Low ||
                candidate.Category is FindingCategory.CodeStyle)
            {
                continue;
            }

            var coveringIndex = restored.FindIndex(existing => CoversFinding(existing, candidate));
            if (coveringIndex >= 0)
            {
                if (GetSeverityRank(candidate.Severity) < GetSeverityRank(restored[coveringIndex].Severity))
                {
                    restored[coveringIndex] = candidate;
                }

                continue;
            }

            restored.Add(candidate);
        }

        return restored;
    }

    private static int GetSeverityRank(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.Critical => 0,
            FindingSeverity.High => 1,
            FindingSeverity.Medium => 2,
            FindingSeverity.Low => 3,
            _ => 4
        };
    }

    private static bool CoversFinding(ReviewFinding existing, ReviewFinding candidate)
    {
        if (!PathsMatch(existing.File, candidate.File))
        {
            return false;
        }

        if (LineRangesAreClose(existing, candidate))
        {
            return true;
        }

        var existingText = $"{existing.Title} {existing.Description} {existing.Suggestion}";
        var candidateTerms = ExtractSignificantTerms($"{candidate.Title} {candidate.Description}");
        if (candidateTerms.Count == 0)
        {
            return false;
        }

        var matches = candidateTerms.Count(term =>
            existingText.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matches >= Math.Min(3, candidateTerms.Count);
    }

    private static bool LineRangesAreClose(ReviewFinding existing, ReviewFinding candidate)
    {
        var existingStart = existing.StartLine;
        var candidateStart = candidate.StartLine;
        if (existingStart <= 0 || candidateStart <= 0)
        {
            return false;
        }

        var existingEnd = Math.Max(existingStart, existing.EndLine);
        var candidateEnd = Math.Max(candidateStart, candidate.EndLine);
        return candidateStart <= existingEnd + 8 && existingStart <= candidateEnd + 8;
    }

    private static IReadOnlyList<string> ExtractSignificantTerms(string text)
    {
        return Regex.Matches(text, @"[\p{L}\p{N}_]{5,}", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private async Task<(IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<ReviewOpportunityItem> Opportunities)>
        RunFinalModelNormalizationAsync(
            IReadOnlyList<ReviewFinding> findings,
            IReadOnlyList<ReviewOpportunityItem> opportunities,
            CancellationToken cancellationToken)
    {
        if (findings.Count == 0 && opportunities.Count == 0)
        {
            return (findings, opportunities);
        }

        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            null,
            [],
            cancellationToken);

        var response = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = 0,
                ExpectJson = true,
                SystemPrompt = reviewPromptFactory.BuildFinalNormalizationSystemPrompt(),
                UserPrompt = reviewPromptFactory.BuildFinalNormalizationUserPrompt(findings, opportunities)
            },
            cancellationToken);

        var parsed = chunkReviewResponseParser.ParseChunkResponse(response);
        return (parsed.Findings, parsed.Opportunities);
    }

    private async Task<IReadOnlyList<string>> ReviewChunksAsync(
        string description,
        IReadOnlyList<string> chunks,
        DiffAcquisitionResult diffResult,
        ReviewExecutionRequest request,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            request.ProviderProfileId,
            request.StageOverrides,
            cancellationToken);

        var responses = new string[chunks.Count];
        var totalIterations = Math.Max(1, chunks.Count);
        var maxConcurrency = Math.Clamp(
            reviewPipelineOptions.Value.MaxConcurrentChunkReviews,
            1,
            totalIterations);
        var completedChunks = 0;

        if (chunks.Count == 0)
        {
            return responses;
        }

        await PersistAndPublishAsync(
            run,
            $"Старт ревью {chunks.Count} чанков, параллельность {maxConcurrency}",
            ReviewPipelineStage.ChunkReview,
            45,
            cancellationToken);

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = Enumerable.Range(0, chunks.Count)
            .Select(async index =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var response = await llmCompletionService.CompleteAsync(
                        selection.Profile,
                        new LlmChatRequest
                        {
                            Model = selection.Model,
                            Temperature = selection.Temperature,
                            ExpectJson = true,
                            SystemPrompt = reviewPromptFactory.BuildChunkReviewSystemPrompt(
                                description,
                                ReviewPromptSpecialRules.PrimaryReviewToolRequestRules),
                            UserPrompt = reviewPromptFactory.BuildUserPrompt(
                                ReviewPipelineStage.ChunkReview,
                                BuildInitialChunkReviewPayload(chunks[index]))
                        },
                        cancellationToken);

                    var finalResponse = await MaybeCompleteChunkReviewWithAdditionalContextAsync(
                        response,
                        description,
                        chunks[index],
                        diffResult,
                        selection,
                        run,
                        index,
                        cancellationToken);

                    responses[index] = finalResponse;
                    var completed = Interlocked.Increment(ref completedChunks);
                    var progress = 45 + (int)Math.Round((completed / (double)totalIterations) * 25d);
                    await PersistAndPublishAsync(
                        run,
                        $"Завершено {completed} из {chunks.Count} чанков ревью",
                        ReviewPipelineStage.ChunkReview,
                        progress,
                        cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        return responses;
    }

    private async Task<string> MaybeCompleteChunkReviewWithAdditionalContextAsync(
        string initialResponse,
        string reviewDescription,
        string chunk,
        DiffAcquisitionResult diffResult,
        StageRouteSelection selection,
        ReviewRun run,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        var envelope = ParseChunkReviewAgentEnvelope(initialResponse);
        LogPrimaryToolRequestDecision(run, chunkIndex, chunk, envelope);
        if (!envelope.NeedMoreContext || envelope.ToolRequests.Count == 0)
        {
            return initialResponse;
        }

        var toolResponses = await reviewWorkspaceToolExecutor.ExecuteAsync(
            diffResult,
            ExtractChunkFilePath(chunk),
            envelope.ToolRequests,
            cancellationToken);
        LogPrimaryToolResponses(run, chunkIndex, toolResponses);

        if (toolResponses.Count == 0)
        {
            logger.LogInformation(
                "Primary tool loop could not fulfill requests for run {RunId}, chunk {ChunkIndex}",
                run.Id,
                chunkIndex + 1);
            return initialResponse;
        }

        var finalPayload = BuildChunkReviewPayload(chunk, toolResponses);
        logger.LogInformation(
            "Primary tool loop triggering final pass for run {RunId}, chunk {ChunkIndex} with {ToolResponseCount} tool results",
            run.Id,
            chunkIndex + 1,
            toolResponses.Count);
        return await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = selection.Temperature,
                ExpectJson = true,
                SystemPrompt = reviewPromptFactory.BuildChunkReviewSystemPrompt(
                    reviewDescription,
                    ReviewPromptSpecialRules.PrimaryReviewFinalizationRules),
                UserPrompt = reviewPromptFactory.BuildUserPrompt(ReviewPipelineStage.ChunkReview, finalPayload)
            },
            cancellationToken);
    }

    private static ChunkReviewAgentEnvelope ParseChunkReviewAgentEnvelope(string raw)
    {
        var normalized = NormalizeJsonPayload(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ChunkReviewAgentEnvelope([], [], false, []);
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ChunkReviewAgentEnvelope([], [], false, []);
            }

            var root = document.RootElement;
            var needMoreContext = root.TryGetProperty("need_more_context", out var needMoreContextNode) &&
                                  needMoreContextNode.ValueKind == JsonValueKind.True;

            var requests = root.TryGetProperty("tool_requests", out var requestsNode) &&
                           requestsNode.ValueKind == JsonValueKind.Array
                ? requestsNode.EnumerateArray()
                    .Select(MapToolRequest)
                    .Where(item => item is not null)
                    .Cast<ReviewWorkspaceToolRequest>()
                    .ToArray()
                : [];

            return new ChunkReviewAgentEnvelope([], [], needMoreContext, requests);
        }
        catch (JsonException)
        {
            return new ChunkReviewAgentEnvelope([], [], false, []);
        }
    }

    private static ReviewWorkspaceToolRequest? MapToolRequest(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var toolName = ReadString(element, "tool_name");
        var reason = ReadString(element, "reason") ?? string.Empty;
        var query = ReadString(element, "query") ?? string.Empty;
        var filePath = ReadString(element, "file_path");
        var pathScope = ReadString(element, "path_scope") ?? string.Empty;
        var startLine = ReadInt(element, "start_line");
        var maxLines = ReadInt(element, "max_lines");

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        return new ReviewWorkspaceToolRequest(
            toolName.Trim(),
            reason.Trim(),
            query.Trim(),
            (filePath ?? string.Empty).Trim(),
            pathScope.Trim(),
            startLine > 0 ? startLine : 1,
            maxLines > 0 ? maxLines : 120);
    }

    private static string BuildChunkReviewPayload(
        string chunk,
        IReadOnlyList<ReviewWorkspaceToolResponse> toolResponses)
    {
        if (toolResponses.Count == 0)
        {
            return chunk;
        }

        var builder = new StringBuilder();
        builder.AppendLine(chunk.Trim());
        builder.AppendLine();
        builder.AppendLine("Supplemental tool results:");

        foreach (var response in toolResponses)
        {
            builder.AppendLine($"Tool: {response.ToolName}");
            builder.AppendLine($"Source: {response.Source}");
            if (!string.IsNullOrWhiteSpace(response.FilePath))
            {
                builder.AppendLine($"File: {response.FilePath}");
            }
            if (response.StartLine > 0)
            {
                builder.AppendLine($"Lines: {response.StartLine}-{response.EndLine}");
            }
            builder.AppendLine("Content:");
            builder.AppendLine(TrimForPrompt(response.Content, 12000));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildInitialChunkReviewPayload(string chunk)
    {
        var filePath = ExtractChunkFilePath(chunk);
        if (string.IsNullOrWhiteSpace(filePath) || filePath == "(unknown file)")
        {
            return chunk;
        }

        return $"Current file: {filePath}\n\n{chunk}";
    }

    private void LogPrimaryToolRequestDecision(
        ReviewRun run,
        int chunkIndex,
        string chunk,
        ChunkReviewAgentEnvelope envelope)
    {
        logger.LogInformation(
            "Primary chunk tool decision for run {RunId}, chunk {ChunkIndex}: NeedMoreContext={NeedMoreContext}, ToolRequestsCount={ToolRequestsCount}, ChunkFile={ChunkFile}",
            run.Id,
            chunkIndex + 1,
            envelope.NeedMoreContext,
            envelope.ToolRequests.Count,
            ExtractChunkFilePath(chunk));

        for (var index = 0; index < envelope.ToolRequests.Count; index++)
        {
            var request = envelope.ToolRequests[index];
            logger.LogInformation(
                "Primary chunk tool request {RequestIndex} for run {RunId}, chunk {ChunkIndex}: Tool={ToolName}, Query={Query}, FilePath={FilePath}, PathScope={PathScope}, StartLine={StartLine}, MaxLines={MaxLines}, Reason={Reason}",
                index + 1,
                run.Id,
                chunkIndex + 1,
                request.ToolName,
                string.IsNullOrWhiteSpace(request.Query) ? "<none>" : TrimForPrompt(request.Query, 160),
                string.IsNullOrWhiteSpace(request.FilePath) ? "<none>" : request.FilePath,
                string.IsNullOrWhiteSpace(request.PathScope) ? "<none>" : request.PathScope,
                request.StartLine,
                request.MaxLines,
                string.IsNullOrWhiteSpace(request.Reason) ? "<none>" : TrimForPrompt(request.Reason, 220));
        }
    }

    private void LogPrimaryToolResponses(
        ReviewRun run,
        int chunkIndex,
        IReadOnlyList<ReviewWorkspaceToolResponse> toolResponses)
    {
        logger.LogInformation(
            "Primary chunk tool responses for run {RunId}, chunk {ChunkIndex}: ToolResponsesCount={ToolResponsesCount}",
            run.Id,
            chunkIndex + 1,
            toolResponses.Count);

        for (var index = 0; index < toolResponses.Count; index++)
        {
            var response = toolResponses[index];
            logger.LogInformation(
                "Primary chunk tool response {ResponseIndex} for run {RunId}, chunk {ChunkIndex}: Tool={ToolName}, FilePath={FilePath}, Source={Source}, Lines={StartLine}-{EndLine}, Preview={Preview}",
                index + 1,
                run.Id,
                chunkIndex + 1,
                response.ToolName,
                response.FilePath,
                response.Source,
                response.StartLine,
                response.EndLine,
                BuildPreview(response.Content));
        }
    }

    private static string NormalizeJsonPayload(string raw)
    {
        var payload = raw?.Trim() ?? string.Empty;
        if (!payload.StartsWith("```", StringComparison.Ordinal))
        {
            return payload;
        }

        return payload.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static string TrimForPrompt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }

    private static string ExtractChunkFilePath(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk))
        {
            return "(unknown file)";
        }

        var lines = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("## File: '", StringComparison.Ordinal))
            {
                return line["## File: '".Length..].TrimEnd('\'', ' ');
            }

            if (line.StartsWith("File: ", StringComparison.OrdinalIgnoreCase))
            {
                return line["File: ".Length..].Trim();
            }

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                return line["+++ b/".Length..].Trim();
            }
        }

        return "(unknown file)";
    }

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var preview = string.Join(
            " / ",
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(3));

        return TrimForPrompt(preview, 220);
    }

    private async Task PersistAndPublishAsync(
        ReviewRun run,
        string message,
        ReviewPipelineStage stage,
        int percent,
        CancellationToken cancellationToken)
    {
        run.Advance(stage, percent, message);
        var update = new ReviewProgressUpdate(run.Id, run.Status, stage, percent, message, DateTimeOffset.UtcNow, false);
        run.RecordProgress(update);
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        await reviewProgressStore.PublishAsync(update, cancellationToken);
    }

    private static bool HasNoChangesSincePreviousReview(ReviewRun previousRun, PreprocessedDiff preprocessed)
    {
        if (previousRun.Status != ReviewRunStatus.Completed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(previousRun.Artifacts.DiffText))
        {
            return false;
        }

        if (!string.Equals(previousRun.Artifacts.DiffText, preprocessed.FilteredDiffText, StringComparison.Ordinal))
        {
            return false;
        }

        return previousRun.Artifacts.ChangedFiles.SequenceEqual(preprocessed.ChangedFiles, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<ReviewedFileArtifact>> BuildReviewedFilesAsync(
        DiffAcquisitionResult diffResult,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken)
    {
        var sections = ParseDiffSections(diffResult.DiffText);
        var results = new List<ReviewedFileArtifact>(sections.Count);

        foreach (var section in sections)
        {
            var selectedRevision = section.ChangeType == "Deleted"
                ? diffResult.TargetRef
                : diffResult.SourceRef;
            var fullContent = !string.IsNullOrWhiteSpace(diffResult.RepositoryPath) && !string.IsNullOrWhiteSpace(selectedRevision)
                ? await repositoryFileContentService.TryGetFileContentAsync(
                    diffResult.RepositoryPath,
                    selectedRevision,
                    section.FilePath,
                    cancellationToken) ?? string.Empty
                : string.Empty;

            var threads = inlineComments
                .Where(comment => PathsMatch(comment.FilePath, section.FilePath))
                .OrderBy(comment => comment.LineNumber)
                .Select(comment => EnrichInlineThread(comment, fullContent, section.Patch))
                .ToArray();

            results.Add(new ReviewedFileArtifact
            {
                FilePath = section.FilePath,
                DisplayName = Path.GetFileName(section.FilePath),
                ChangeType = section.ChangeType,
                AddedLines = section.AddedLines,
                DeletedLines = section.DeletedLines,
                DiffPatch = section.Patch,
                FullContent = fullContent,
                ChangedLineNumbers = section.ChangedLineNumbers,
                InlineThreads = threads
            });
        }

        return results;
    }

    private static InlineCommentDraft EnrichInlineThread(
        InlineCommentDraft thread,
        string fullContent,
        string diffPatch)
    {
        var normalizedThread = NormalizeThreadLocation(thread, fullContent);
        var context = ContextExtractor.Build(
            normalizedThread.LineNumber,
            normalizedThread.StartLine,
            normalizedThread.EndLine,
            fullContent,
            diffPatch,
            normalizedThread.ExistingCode);
        var relevantDiffHunk = ContextExtractor.BuildRelevantDiffHunk(
            normalizedThread.LineNumber,
            diffPatch,
            normalizedThread.ExistingCode,
            context.StartLine,
            context.EndLine);
        return normalizedThread with
        {
            ContextBlock = context.Block,
            ContextStartLine = context.StartLine,
            ContextEndLine = context.EndLine,
            RelevantDiffHunk = relevantDiffHunk
        };
    }

    private static InlineCommentDraft NormalizeThreadLocation(InlineCommentDraft thread, string fullContent)
    {
        if (thread.LineNumber <= 0)
        {
            return thread;
        }

        if (thread.StartLine <= 0 || thread.EndLine < thread.StartLine)
        {
            return thread with
            {
                StartLine = thread.LineNumber,
                EndLine = thread.LineNumber
            };
        }

        var normalizedEndLine = thread.EndLine >= thread.StartLine ? thread.EndLine : thread.StartLine;
        var providedSpan = normalizedEndLine - thread.StartLine;
        var snippetRange = ContextExtractor.TryLocateSnippetRangeInFile(
            fullContent,
            thread.ExistingCode,
            thread.LineNumber);

        if (snippetRange is not null)
        {
            var snippetSpan = snippetRange.Value.EndLine - snippetRange.Value.StartLine;
            var shouldPreferSnippetRange =
                providedSpan > 24 ||
                thread.LineNumber < thread.StartLine ||
                thread.LineNumber > normalizedEndLine ||
                (thread.LineNumber >= snippetRange.Value.StartLine &&
                 thread.LineNumber <= snippetRange.Value.EndLine &&
                 snippetSpan <= providedSpan);

            if (shouldPreferSnippetRange)
            {
                return thread with
                {
                    StartLine = snippetRange.Value.StartLine,
                    EndLine = snippetRange.Value.EndLine
                };
            }
        }

        if (thread.LineNumber >= thread.StartLine &&
            thread.LineNumber <= normalizedEndLine &&
            providedSpan <= 24)
        {
            return thread;
        }

        return thread with
        {
            StartLine = thread.LineNumber,
            EndLine = thread.LineNumber
        };
    }

    private static IReadOnlyList<InlineCommentDraft> FlattenInlineThreads(
        IReadOnlyList<ReviewedFileArtifact> reviewedFiles)
    {
        return reviewedFiles
            .SelectMany(file => file.InlineThreads)
            .OrderBy(comment => NormalizePath(comment.FilePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(comment => comment.LineNumber)
            .ThenBy(comment => comment.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DiffFileSection> ParseDiffSections(string diffText)
    {
        var sections = new List<DiffFileSection>();
        var marker = "diff --git ";
        var parts = diffText.Split(marker, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var patch = $"{marker}{part}";
            var lines = patch.Split('\n');
            var header = lines.FirstOrDefault() ?? string.Empty;
            var oldPath = ExtractPath(header, "a/");
            var newPath = ExtractPath(header, "b/");

            var changeType = "Modified";
            if (lines.Any(line => line.StartsWith("new file mode", StringComparison.Ordinal)))
            {
                changeType = "Added";
            }
            else if (lines.Any(line => line.StartsWith("deleted file mode", StringComparison.Ordinal)))
            {
                changeType = "Deleted";
            }
            else if (lines.Any(line => line.StartsWith("rename from ", StringComparison.Ordinal)))
            {
                changeType = "Renamed";
            }

            var filePath = changeType == "Deleted" ? oldPath : newPath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = !string.IsNullOrWhiteSpace(newPath) ? newPath : oldPath;
            }

            var addedLines = 0;
            var deletedLines = 0;
            var changedLineNumbers = new List<int>();
            var currentNewLine = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    currentNewLine = ParseNewLineStart(line) - 1;
                    continue;
                }

                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    addedLines++;
                    currentNewLine++;
                    changedLineNumbers.Add(currentNewLine);
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    deletedLines++;
                    continue;
                }

                if (line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\\", StringComparison.Ordinal))
                {
                    currentNewLine++;
                }
            }

            sections.Add(new DiffFileSection(
                filePath.Trim(),
                patch,
                changeType,
                addedLines,
                deletedLines,
                changedLineNumbers.Distinct().ToArray()));
        }

        return sections;
    }

    private static string ExtractPath(string header, string marker)
    {
        var start = header.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = header.IndexOf(' ', start);
        return (end > start ? header[start..end] : header[start..]).Trim();
    }

    private static int ParseNewLineStart(string hunkHeader)
    {
        var plusIndex = hunkHeader.IndexOf('+');
        if (plusIndex < 0)
        {
            return 1;
        }

        var digits = new string(hunkHeader[(plusIndex + 1)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 1;
    }

    private static bool PathsMatch(string left, string right)
    {
        return NormalizePath(left) == NormalizePath(right);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).TrimStart('/').ToLowerInvariant();
    }

    private sealed record ParsedChangeDescription(
        string Description,
        ChangeDescriptionStructuredContent? StructuredContent);

    private static class ContextExtractor
    {
        public static (string Block, int StartLine, int EndLine) Build(
            int lineNumber,
            int startLine,
            int endLine,
            string fullContent,
            string diffPatch,
            string snippet)
        {
            var fromFile = BuildFromFullFile(lineNumber, startLine, endLine, fullContent, snippet);
            if (!string.IsNullOrWhiteSpace(fromFile.Block))
            {
                return fromFile;
            }

            return BuildFromPatch(diffPatch, snippet);
        }

        private static (string Block, int StartLine, int EndLine) BuildFromFullFile(
            int lineNumber,
            int startLine,
            int endLine,
            string fullContent,
            string snippet)
        {
            if (string.IsNullOrWhiteSpace(fullContent))
            {
                return (string.Empty, 0, 0);
            }

            var lines = fullContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var snippetRange = TryLocateSnippetRangeInFile(
                lines,
                snippet,
                lineNumber > 0 ? lineNumber : startLine);
            var anchorStartLine = startLine > 0 ? Math.Min(startLine, lines.Length) : 0;
            var anchorEndLine = endLine > 0 ? Math.Min(endLine, lines.Length) : 0;

            if (snippetRange is not null &&
                lineNumber > 0 &&
                lineNumber >= snippetRange.Value.StartLine &&
                lineNumber <= snippetRange.Value.EndLine)
            {
                anchorStartLine = snippetRange.Value.StartLine;
                anchorEndLine = snippetRange.Value.EndLine;
            }
            else if (anchorStartLine == 0 && snippetRange is not null)
            {
                anchorStartLine = snippetRange.Value.StartLine;
                anchorEndLine = snippetRange.Value.EndLine;
            }

            if (anchorStartLine == 0 && lineNumber > 0 && lineNumber <= lines.Length)
            {
                anchorStartLine = lineNumber;
                anchorEndLine = lineNumber;
            }

            if (anchorStartLine <= 0)
            {
                return (string.Empty, 0, 0);
            }

            anchorEndLine = anchorEndLine >= anchorStartLine ? anchorEndLine : anchorStartLine;

            var blockStartLine = Math.Max(1, anchorStartLine - 6);
            var blockEndLine = Math.Min(lines.Length, anchorEndLine + 10);

            var declarationStart = FindDeclarationStart(lines, anchorStartLine);
            if (declarationStart > 0)
            {
                blockStartLine = Math.Min(blockStartLine, declarationStart);
            }

            if (anchorEndLine > blockEndLine)
            {
                blockEndLine = anchorEndLine;
            }

            var contextLines = new List<string>(blockEndLine - blockStartLine + 1);
            for (var currentLine = blockStartLine; currentLine <= blockEndLine; currentLine++)
            {
                contextLines.Add($"{currentLine,4}: {lines[currentLine - 1]}");
            }

            return (string.Join('\n', contextLines), blockStartLine, blockEndLine);
        }

        private static int FindDeclarationStart(IReadOnlyList<string> lines, int lineNumber)
        {
            var minLine = Math.Max(1, lineNumber - 12);
            for (var currentLine = lineNumber; currentLine >= minLine; currentLine--)
            {
                var trimmed = lines[currentLine - 1].Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (trimmed.Contains(" class ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("class ", StringComparison.Ordinal) ||
                    trimmed.Contains(" record ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("record ", StringComparison.Ordinal) ||
                    trimmed.Contains(" interface ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("interface ", StringComparison.Ordinal) ||
                    trimmed.Contains(" enum ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("protected ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("internal ", StringComparison.Ordinal))
                {
                    return currentLine;
                }
            }

            return 0;
        }

        private static (string Block, int StartLine, int EndLine) BuildFromPatch(string diffPatch, string snippet)
        {
            if (string.IsNullOrWhiteSpace(diffPatch))
            {
                return (string.Empty, 0, 0);
            }

            var lines = diffPatch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var anchorIndex = FindAnchorIndex(lines, snippet);
            if (anchorIndex < 0)
            {
                anchorIndex = Array.FindIndex(lines, line => line.StartsWith("@@", StringComparison.Ordinal));
            }

            if (anchorIndex < 0)
            {
                return (string.Empty, 0, 0);
            }

            var startIndex = Math.Max(0, anchorIndex - 4);
            var endIndex = Math.Min(lines.Length - 1, anchorIndex + 8);
            return (string.Join('\n', lines[startIndex..(endIndex + 1)]), 0, 0);
        }

        private static int FindAnchorIndex(IReadOnlyList<string> lines, string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return -1;
            }

            var snippetCandidates = snippet
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeForMatch)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .OrderByDescending(line => line.Length)
                .Take(3)
                .ToArray();

            if (snippetCandidates.Length == 0)
            {
                return -1;
            }

            var bestIndex = -1;
            var bestScore = 0d;
            for (var index = 0; index < lines.Count; index++)
            {
                var diffLine = lines[index];
                if (!(diffLine.StartsWith("+", StringComparison.Ordinal) || diffLine.StartsWith(" ", StringComparison.Ordinal)))
                {
                    continue;
                }

                var normalizedDiffLine = NormalizeForMatch(diffLine[1..]);
                foreach (var candidate in snippetCandidates)
                {
                    var score = normalizedDiffLine.Contains(candidate, StringComparison.Ordinal) ||
                                candidate.Contains(normalizedDiffLine, StringComparison.Ordinal)
                        ? 0.95d
                        : 0d;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = index;
                    }
                }
            }

            return bestIndex;
        }

        private static string NormalizeForMatch(string value)
        {
            return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray())
                .Trim()
                .ToLowerInvariant();
        }

        public static (int StartLine, int EndLine)? TryLocateSnippetRangeInFile(
            string fullContent,
            string snippet,
            int preferredLine = 0)
        {
            if (string.IsNullOrWhiteSpace(fullContent))
            {
                return null;
            }

            var lines = fullContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            return TryLocateSnippetRangeInFile(lines, snippet, preferredLine);
        }

        private static (int StartLine, int EndLine)? TryLocateSnippetRangeInFile(
            IReadOnlyList<string> lines,
            string snippet,
            int preferredLine = 0)
        {
            if (string.IsNullOrWhiteSpace(snippet) || lines.Count == 0)
            {
                return null;
            }

            var snippetCandidates = snippet
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeForMatch)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct()
                .OrderByDescending(line => line.Length)
                .Take(6)
                .ToArray();

            if (snippetCandidates.Length == 0)
            {
                return null;
            }

            var candidateMatches = new List<int[]>(snippetCandidates.Length);
            foreach (var candidate in snippetCandidates)
            {
                var matches = new List<int>();
                for (var index = 0; index < lines.Count; index++)
                {
                    var normalizedLine = NormalizeForMatch(lines[index]);
                    if (string.IsNullOrWhiteSpace(normalizedLine))
                    {
                        continue;
                    }

                    if (!normalizedLine.Contains(candidate, StringComparison.Ordinal) &&
                        !candidate.Contains(normalizedLine, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matches.Add(index + 1);
                }

                if (matches.Count > 0)
                {
                    candidateMatches.Add(matches.ToArray());
                }
            }

            if (candidateMatches.Count == 0)
            {
                return null;
            }

            if (preferredLine > 0)
            {
                var nearestMatches = candidateMatches
                    .Select(matches => matches
                        .OrderBy(line => Math.Abs(line - preferredLine))
                        .First())
                    .Distinct()
                    .OrderBy(line => line)
                    .ToArray();

                var localCluster = nearestMatches
                    .Where(line => Math.Abs(line - preferredLine) <= 12)
                    .ToArray();

                if (localCluster.Length == 0)
                {
                    var seed = nearestMatches
                        .OrderBy(line => Math.Abs(line - preferredLine))
                        .First();
                    localCluster = nearestMatches
                        .Where(line => Math.Abs(line - seed) <= 12)
                        .ToArray();
                }

                if (localCluster.Length > 0)
                {
                    return (localCluster.Min(), localCluster.Max());
                }
            }

            var fallbackMatches = candidateMatches
                .Select(matches => matches[0])
                .OrderBy(line => line)
                .ToArray();

            return (fallbackMatches.Min(), fallbackMatches.Max());
        }

        public static string BuildRelevantDiffHunk(
            int lineNumber,
            string diffPatch,
            string snippet,
            int contextStartLine,
            int contextEndLine)
        {
            if (string.IsNullOrWhiteSpace(diffPatch))
            {
                return string.Empty;
            }

            var lines = diffPatch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var hunks = ParseHunks(lines);
            if (hunks.Count == 0)
            {
                return diffPatch.Trim();
            }

            var byLineNumber = hunks.FirstOrDefault(hunk =>
                lineNumber > 0 &&
                hunk.NewLineStart <= lineNumber &&
                lineNumber <= hunk.NewLineEnd);
            if (byLineNumber is not null)
            {
                return byLineNumber.Content;
            }

            var byContextRange = hunks.FirstOrDefault(hunk =>
                contextStartLine > 0 &&
                contextEndLine > 0 &&
                Math.Max(hunk.NewLineStart, contextStartLine) <= Math.Min(hunk.NewLineEnd, contextEndLine));
            if (byContextRange is not null)
            {
                return byContextRange.Content;
            }

            var snippetAnchorIndex = FindAnchorIndex(lines, snippet);
            if (snippetAnchorIndex >= 0)
            {
                var bySnippet = hunks.FirstOrDefault(hunk =>
                {
                    var hunkLines = hunk.Content.Split('\n');
                    return snippetAnchorIndex >= hunk.OriginalStartIndex &&
                           snippetAnchorIndex <= hunk.OriginalStartIndex + hunkLines.Length - 1;
                });

                if (bySnippet is not null)
                {
                    return bySnippet.Content;
                }
            }

            return hunks[0].Content;
        }

        private static IReadOnlyList<DiffHunk> ParseHunks(IReadOnlyList<string> lines)
        {
            var result = new List<DiffHunk>();
            DiffHunk? currentHunk = null;
            var currentLines = new List<string>();

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (currentHunk is not null)
                    {
                        result.Add(currentHunk with { Content = string.Join('\n', currentLines) });
                        currentLines = new List<string>();
                    }

                    var parsed = ParseHunkHeader(line);
                    currentHunk = new DiffHunk(parsed.NewLineStart, parsed.NewLineEnd, string.Empty, index);
                }

                if (currentHunk is not null)
                {
                    currentLines.Add(line);
                }
            }

            if (currentHunk is not null)
            {
                result.Add(currentHunk with { Content = string.Join('\n', currentLines) });
            }

            return result;
        }

        private static (int NewLineStart, int NewLineEnd) ParseHunkHeader(string header)
        {
            var startMatch = System.Text.RegularExpressions.Regex.Match(
                header,
                @"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!startMatch.Success)
            {
                return (1, 1);
            }

            var start = int.Parse(startMatch.Groups["start"].Value);
            var count = startMatch.Groups["count"].Success && int.TryParse(startMatch.Groups["count"].Value, out var parsedCount)
                ? parsedCount
                : 1;
            var end = Math.Max(start, start + Math.Max(count - 1, 0));
            return (start, end);
        }
    }

    private sealed record DiffFileSection(
        string FilePath,
        string Patch,
        string ChangeType,
        int AddedLines,
        int DeletedLines,
        IReadOnlyList<int> ChangedLineNumbers);

    private sealed record DiffHunk(int NewLineStart, int NewLineEnd, string Content, int OriginalStartIndex);
}
