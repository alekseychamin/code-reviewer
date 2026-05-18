using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Models.Graph;
using TfsReviewPlatform.Application.Prompts;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRunExecutor(
    IReviewRunRepository reviewRunRepository,
    IReviewProgressStore reviewProgressStore,
    IReviewSemanticIndex reviewSemanticIndex,
    IReviewCodeSemanticContextService reviewCodeSemanticContextService,
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
    IExternalReviewEngine externalReviewEngine,
    IDeepSeekTuiReviewEngine deepSeekTuiReviewEngine,
    IExternalReviewArtifactParser externalReviewArtifactParser,
    IFindingsComparisonService findingsComparisonService,
    IMarkdownReportBuilder markdownReportBuilder,
    IReviewPublisher reviewPublisher,
    IOptions<ReviewPipelineOptions> reviewPipelineOptions,
    IOptions<ExternalReviewOptions> externalReviewOptions,
    IOptions<DeepSeekTuiReviewOptions> deepSeekTuiReviewOptions,
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
        string? roslynWorkspaceRoot = null;
        CodeGraph? roslynGraph = null;
        var mandatoryFindings = new List<ReviewFinding>();
        Task<ExternalReviewArtifact>? externalReviewTask = null;

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

            if (!request.ForceRerun && previousRun is not null && HasNoChangesSincePreviousReview(previousRun, preprocessed))
            {
                await ReusePreviousReviewResultAsync(run, previousRun, cancellationToken);
                return;
            }

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
                    roslynWorkspaceRoot = ws;

                    await PersistAndPublishAsync(
                        run,
                        "Ход выполнения пайплайна: построение graph (RoslynGraphBuilder)",
                        ReviewPipelineStage.Preprocessing,
                        22,
                        cancellationToken);

                    var graph = await roslynGraphBuilder.BuildAsync(ws, sln, preprocessed.ChangedFiles, cancellationToken);
                    roslynGraph = graph;
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

            var reviewScope = await BuildIncrementalReviewScopeAsync(
                previousRun,
                preprocessed,
                request,
                run,
                roslynGraph,
                roslynWorkspaceRoot,
                pipelineOptsForRoslyn,
                cancellationToken);
            var reviewPreprocessed = reviewScope.ReviewPreprocessed;
            if (reviewScope.IsIncremental)
            {
                await PersistAndPublishAsync(
                    run,
                    $"Incremental review: модель получит {reviewPreprocessed.ChangedFiles.Count} delta-файлов из {preprocessed.ChangedFiles.Count}",
                    ReviewPipelineStage.ChangeDescription,
                    32,
                    cancellationToken);
            }

            var externalReviewInput = new ExternalReviewInput
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                RiskDomainsSummary = ReviewRiskDomainFormatter.BuildAllRiskDomainsBlock(preprocessed.RiskDomains),
                RepositoryName = diffResult.RepositoryName,
                ServiceName = diffResult.ServiceName,
                PullRequestTitle = diffResult.PullRequestTitle,
                PullRequestUrl = diffResult.PullRequestUrl ?? run.Target.PullRequestUrl,
                RepositoryPath = diffResult.RepositoryPath,
                RepositoryRemoteUrl = diffResult.RepositoryRemoteUrl,
                GitFetchSourceRef = diffResult.GitFetchSourceRef,
                GitFetchTargetRef = diffResult.GitFetchTargetRef,
                GitHttpExtraHeader = diffResult.GitHttpExtraHeader,
                SourceRef = diffResult.SourceRef,
                TargetRef = diffResult.TargetRef
            };
            externalReviewTask = StartExternalReviewAsync(
                run,
                externalReviewInput,
                cancellationToken);
            if (externalReviewTask is not null)
            {
                run.UpdateArtifacts(CopyArtifactsWithSemanticCodeContext(
                    run.Artifacts,
                    run.Artifacts.SemanticCodeContext,
                    new ExternalReviewArtifact
                    {
                        Enabled = true,
                        Attempted = true,
                        EngineName = externalReviewOptions.Value.EngineName,
                        Status = "running",
                        Message = "External review sidecar is running.",
                        StartedAt = DateTimeOffset.UtcNow
                    }));
                await reviewRunRepository.UpdateAsync(run, cancellationToken);
                _ = TrackExternalReviewCompletionAsync(externalReviewTask, run);
            }

            var externalChangeSummary = await TryBuildExternalChangeSummaryAsync(externalReviewTask, run, cancellationToken);
            var changeSummary = externalChangeSummary
                                ?? await GenerateChangeSummaryAsync(run, run.DisplayTitle, preprocessed, request, cancellationToken);
            var description = changeSummary.Description;
            var reviewDescription = reviewScope.IsIncremental
                ? $"{description}\n\nIncremental review mode: review only the provided delta diff sections. Baseline findings are known context; do not repeat them unless the delta introduces a new, distinct issue."
                : description;
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
            await PersistAndPublishAsync(
                run,
                externalChangeSummary is null ? "Change description generated" : "Change description generated by PR-Agent",
                ReviewPipelineStage.ChunkReview,
                45,
                cancellationToken);

            ExternalReviewArtifact? externalReviewForFindings = null;
            var externalInsights = ExternalReviewInsights.Empty;
            DeepSeekTuiReviewResult? deepSeekTuiReview = null;
            var useDeepSeekTuiPrimary = deepSeekTuiReviewOptions.Value.Enabled &&
                                        deepSeekTuiReviewOptions.Value.UseAsPrimaryReviewer;
            ExternalScoutReviewResult? externalScoutReview = null;
            if (!useDeepSeekTuiPrimary)
            {
                externalScoutReview = await TryReviewExternalScoutMissingCriticsAsync(
                    reviewDescription,
                    reviewPreprocessed,
                    externalReviewTask,
                    request,
                    run,
                    reviewScope.IsIncremental && previousRun is not null ? previousRun.Findings : [],
                    cancellationToken);
            }

            IReadOnlyList<string> rawFindings;
            if (useDeepSeekTuiPrimary)
            {
                deepSeekTuiReview = await TryRunDeepSeekTuiReviewAsync(
                    run,
                    CopyReviewInputForPrimaryDiff(externalReviewInput, reviewPreprocessed),
                    cancellationToken);
                rawFindings = deepSeekTuiReview.Succeeded ? [] : await RunPrimaryModelReviewAsync(
                    reviewDescription,
                    reviewPreprocessed,
                    diffResult,
                    request,
                    run,
                    cancellationToken);
            }
            else if (externalScoutReview is not null)
            {
                rawFindings = externalScoutReview.RawResponses;
                externalReviewForFindings = externalScoutReview.ExternalReview;
                externalInsights = externalScoutReview.Insights;
            }
            else
            {
                rawFindings = await RunPrimaryModelReviewAsync(
                    reviewDescription,
                    reviewPreprocessed,
                    diffResult,
                    request,
                    run,
                    cancellationToken);
            }
            await PersistAndPublishAsync(run, "Raw findings collected", ReviewPipelineStage.FindingsNormalization, 75, cancellationToken);

            var findings = new List<ReviewFinding>();
            var opportunities = new List<ReviewOpportunityItem>();
            if (externalScoutReview is not null && externalReviewOptions.Value.UseReviewFindings)
            {
                var seedFindings = reviewScope.IsIncremental && previousRun is not null
                    ? FilterIncrementalCandidateFindings(externalInsights.Findings, previousRun, reviewPreprocessed)
                    : externalInsights.Findings;
                findings.AddRange(seedFindings);
                opportunities.AddRange(externalInsights.Opportunities);
                logger.LogInformation(
                    "Seeded external review scout findings for run {RunId}: findings={Findings}, opportunities={Opportunities}",
                    run.Id,
                    seedFindings.Count,
                    externalInsights.Opportunities.Count);
            }

            if (deepSeekTuiReview is { Succeeded: true })
            {
                var seedFindings = reviewScope.IsIncremental && previousRun is not null
                    ? FilterIncrementalCandidateFindings(deepSeekTuiReview.Findings, previousRun, reviewPreprocessed)
                    : deepSeekTuiReview.Findings;
                findings.AddRange(seedFindings);
                opportunities.AddRange(deepSeekTuiReview.Opportunities);
                logger.LogInformation(
                    "Seeded DeepSeek-TUI review findings for run {RunId}: findings={Findings}, opportunities={Opportunities}, workspace={Workspace}",
                    run.Id,
                    seedFindings.Count,
                    deepSeekTuiReview.Opportunities.Count,
                    deepSeekTuiReview.WorkspacePath);
            }

            for (var index = 0; index < rawFindings.Count; index++)
            {
                var raw = rawFindings[index];
                var parsed = chunkReviewResponseParser.ParseChunkResponse(raw);
                logger.LogInformation(
                    "Parsed raw review response for run {RunId}: responseIndex={ResponseIndex}, responseChars={ResponseChars}, findings={Findings}, opportunities={Opportunities}, preview={Preview}",
                    run.Id,
                    index + 1,
                    raw.Length,
                    parsed.Findings.Count,
                    parsed.Opportunities.Count,
                    BuildPreview(raw));
                if (externalScoutReview is null)
                {
                    findings.AddRange(parsed.Findings);
                }
                else
                {
                    var supportedFindings = SuppressContradictedScoutFindings(
                        parsed.Findings,
                        preprocessed.FilteredDiffText,
                        run.Id);
                    findings.AddRange(supportedFindings.Where(finding =>
                        findings.All(existing => !CoversFinding(existing, finding))));
                }

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
                reviewPreprocessed.ReviewHints,
                findings,
                deepSeekTuiReview is { Succeeded: true });
            findings.AddRange(confirmedDeterministicFindings);
            if (confirmedDeterministicFindings.Count > 0)
            {
                var deterministicMode = deepSeekTuiReview is { Succeeded: true }
                    ? "supplemental"
                    : "confirmed";
                logger.LogInformation(
                    "Added {FindingCount} {DeterministicMode} deterministic findings for run {RunId}",
                    confirmedDeterministicFindings.Count,
                    deterministicMode,
                    run.Id);
            }

            if (externalScoutReview is null && externalReviewOptions.Value.UseReviewFindings)
            {
                externalReviewForFindings ??= await CompleteExternalReviewAsync(externalReviewTask, run, cancellationToken);
                externalInsights = ReferenceEquals(externalInsights, ExternalReviewInsights.Empty)
                    ? externalReviewArtifactParser.Parse(externalReviewForFindings)
                    : externalInsights;
                var externalFindingsAdded = 0;
                foreach (var finding in externalInsights.Findings)
                {
                    if (findings.All(existing => !CoversFinding(existing, finding)))
                    {
                        findings.Add(finding);
                        externalFindingsAdded++;
                    }
                }

                if (externalInsights.Opportunities.Count > 0)
                {
                    opportunities.AddRange(externalInsights.Opportunities);
                }

                if (externalFindingsAdded > 0 || externalInsights.Opportunities.Count > 0)
                {
                    logger.LogInformation(
                        "Added external review insights for run {RunId}: findings={Findings}, opportunities={Opportunities}",
                        run.Id,
                        externalFindingsAdded,
                        externalInsights.Opportunities.Count);
                }
            }

            var deepSeekPrimarySucceeded = deepSeekTuiReview is { Succeeded: true };
            if (reviewPipelineOptions.Value.EnableFinalModelNormalizationPass &&
                !deepSeekPrimarySucceeded)
            {
                var findingsBeforeNormalization = findings.ToArray();
                await PersistAndPublishAsync(
                    run,
                    "Финальная нормализация findings через модель",
                    ReviewPipelineStage.FindingsNormalization,
                    76,
                    cancellationToken);
                var normalized = await RunFinalModelNormalizationAsync(
                    run,
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
            else if (reviewPipelineOptions.Value.EnableFinalModelNormalizationPass)
            {
                logger.LogInformation(
                    "Skipped final model normalization for run {RunId} because DeepSeek-TUI primary review succeeded",
                    run.Id);
            }

            var severityNormalizedFindings = NormalizeFindingSeverities(
                findings,
                deepSeekPrimarySucceeded);
            if (!ReferenceEquals(severityNormalizedFindings, findings))
            {
                logger.LogInformation(
                    "Normalized finding severities for run {RunId}: adjusted={Adjusted}",
                    run.Id,
                    CountSeverityChanges(findings, severityNormalizedFindings));
                findings = severityNormalizedFindings.ToList();
            }

            var demotedFindingOpportunities = BuildOpportunitiesFromDemotableFindings(findings);
            if (demotedFindingOpportunities.Count > 0)
            {
                var demotedAdded = 0;
                foreach (var opportunity in demotedFindingOpportunities)
                {
                    if (opportunities.Any(existing => AreEquivalentOpportunities(existing, opportunity)))
                    {
                        continue;
                    }

                    opportunities.Add(opportunity);
                    demotedAdded++;
                }

                logger.LogInformation(
                    "Demoted low-precision findings to opportunities for run {RunId}: demoted={Demoted}, addedOpportunities={AddedOpportunities}",
                    run.Id,
                    demotedFindingOpportunities.Count,
                    demotedAdded);
            }

            var precisionFilteredFindings = SuppressLowPrecisionFindings(
                findings,
                deepSeekPrimarySucceeded);
            if (precisionFilteredFindings.Count != findings.Count)
            {
                logger.LogInformation(
                    "Suppressed low-precision findings for run {RunId}: before={Before}, after={After}",
                    run.Id,
                    findings.Count,
                    precisionFilteredFindings.Count);
                findings = precisionFilteredFindings.ToList();
            }

            if (reviewPipelineOptions.Value.EnableFindingEvidenceGate)
            {
                var evidenceGatedFindings = ApplyFindingEvidenceGate(
                    findings,
                    reviewPreprocessed,
                    deepSeekPrimarySucceeded);
                await PersistAndPublishAsync(
                    run,
                    $"Evidence gate: оставлено {evidenceGatedFindings.Count} из {findings.Count} findings с diff-якорем",
                    ReviewPipelineStage.FindingsNormalization,
                    79,
                    cancellationToken);

                if (evidenceGatedFindings.Count != findings.Count)
                {
                    logger.LogInformation(
                        "Finding evidence gate suppressed unsupported findings for run {RunId}: before={Before}, after={After}",
                        run.Id,
                        findings.Count,
                        evidenceGatedFindings.Count);
                    findings = evidenceGatedFindings.ToList();
                }
            }

            var contradictionFilteredFindings = SuppressContradictedByDiffFindings(
                findings,
                reviewPreprocessed.FilteredDiffText,
                deepSeekPrimarySucceeded);
            if (contradictionFilteredFindings.Count != findings.Count)
            {
                logger.LogInformation(
                    "Suppressed findings contradicted by diff evidence for run {RunId}: before={Before}, after={After}",
                    run.Id,
                    findings.Count,
                    contradictionFilteredFindings.Count);
                findings = contradictionFilteredFindings.ToList();
            }

            if (!deepSeekPrimarySucceeded)
            {
                var precisionFilteredOpportunities = SuppressLowSignalOpportunities(opportunities);
                if (precisionFilteredOpportunities.Count != opportunities.Count)
                {
                    logger.LogInformation(
                        "Suppressed low-signal opportunities for run {RunId}: before={Before}, after={After}",
                        run.Id,
                        opportunities.Count,
                        precisionFilteredOpportunities.Count);
                    opportunities = precisionFilteredOpportunities.ToList();
                }
            }
            else if (opportunities.Count > 0)
            {
                logger.LogInformation(
                    "Skipped low-signal opportunity suppression for run {RunId} because DeepSeek-TUI primary review succeeded: opportunities={Opportunities}",
                    run.Id,
                    opportunities.Count);
            }

            var deduplicatedFindings = DeduplicateFindings(findings, deepSeekPrimarySucceeded);
            if (deduplicatedFindings.Count != findings.Count)
            {
                logger.LogInformation(
                    "Local finding deduplication for run {RunId}: before={Before}, after={After}",
                    run.Id,
                    findings.Count,
                    deduplicatedFindings.Count);
                findings = deduplicatedFindings.ToList();
            }

            if (reviewScope.IsIncremental && previousRun is not null)
            {
                var incrementalFindings = FilterIncrementalCandidateFindings(
                    findings,
                    previousRun,
                    reviewPreprocessed);
                if (incrementalFindings.Count != findings.Count)
                {
                    logger.LogInformation(
                        "Incremental review filtered baseline-covered findings for run {RunId}: before={Before}, after={After}",
                        run.Id,
                        findings.Count,
                        incrementalFindings.Count);
                    findings = incrementalFindings.ToList();
                }
            }

            var primaryOpportunities = opportunities;
            var findingsComparison = previousRun is null
                ? null
                : reviewScope.IsIncremental
                    ? BuildIncrementalFindingsComparison(previousRun, findings)
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
            var externalReview = await CompleteExternalReviewAsync(externalReviewTask, run, cancellationToken);
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
                FindingsComparison = findingsComparison,
                ExternalReview = externalReview
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
                FindingsComparison = findingsComparison,
                ExternalReview = externalReview
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
                FindingsComparison = findingsComparison,
                ExternalReview = externalReview
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

    private static ReviewArtifacts CopyArtifactsWithSemanticCodeContext(
        ReviewArtifacts artifacts,
        SemanticCodeContextArtifact semanticCodeContext,
        ExternalReviewArtifact? externalReview = null)
    {
        return new ReviewArtifacts
        {
            DiffText = artifacts.DiffText,
            PreparedChunks = artifacts.PreparedChunks,
            ChangedFiles = artifacts.ChangedFiles,
            ChangeDescription = artifacts.ChangeDescription,
            ChangeDescriptionStructured = artifacts.ChangeDescriptionStructured,
            ChangeDiagramMermaid = artifacts.ChangeDiagramMermaid,
            MarkdownReport = artifacts.MarkdownReport,
            SummaryComment = artifacts.SummaryComment,
            ReviewDiscussionMessages = artifacts.ReviewDiscussionMessages,
            InlineComments = artifacts.InlineComments,
            ReviewedFiles = artifacts.ReviewedFiles,
            PrimaryOpportunities = artifacts.PrimaryOpportunities,
            FindingsComparison = artifacts.FindingsComparison,
            SemanticCodeContext = semanticCodeContext,
            ExternalReview = externalReview ?? artifacts.ExternalReview,
            ProgressUpdates = artifacts.ProgressUpdates
        };
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
        var deepSeek = deepSeekTuiReviewOptions.Value;
        logger.LogInformation(
            "Review pipeline configuration for run {RunId}: ProviderProfileId={ProviderProfileId}, ForceRerun={ForceRerun}, " +
            "MaxChunkCharacters={MaxChunkCharacters}, MaxPrimaryReviewChunkCharacters={MaxPrimaryReviewChunkCharacters}, " +
            "MergePrimaryReviewChunks={MergePrimaryReviewChunks}, MaxConcurrentChunkReviews={MaxConcurrentChunkReviews}, " +
            "MaxChangeSummaryCharacters={MaxChangeSummaryCharacters}, MaxChunkToolRequests={MaxChunkToolRequests}, RoslynEnabled={RoslynEnabled}, " +
            "SinglePassFullDiffAndGraphPrimaryReview={SinglePassPrimary}, SinglePassFullContextMaxCharacters={SinglePassMax}, " +
            "IncludeRoslynGraphInFullContextPayload={IncludeGraph}, FullContextMaxToolIterations={FullContextMaxToolIterations}, " +
            "EnableDeterministicCoverageCritic={EnableDeterministicCoverageCritic}, EnableFinalModelNormalizationPass={EnableFinalNormalization}, " +
            "EnableFindingEvidenceGate={EnableFindingEvidenceGate}, UseIncrementalReviewMode={UseIncrementalReviewMode}, " +
            "DeepSeekTuiEnabled={DeepSeekTuiEnabled}, DeepSeekTuiUseAsPrimary={DeepSeekTuiUseAsPrimary}",
            run.Id,
            string.IsNullOrWhiteSpace(request.ProviderProfileId) ? "(routing default)" : request.ProviderProfileId,
            request.ForceRerun,
            o.MaxChunkCharacters,
            o.MaxPrimaryReviewChunkCharacters,
            o.MergePrimaryReviewChunks,
            o.MaxConcurrentChunkReviews,
            o.MaxChangeSummaryCharacters,
            o.MaxChunkToolRequests,
            o.Roslyn.Enabled,
            o.SinglePassFullDiffAndGraphPrimaryReview,
            o.SinglePassFullContextMaxCharacters,
            o.IncludeRoslynGraphInFullContextPayload,
            o.FullContextMaxToolIterations,
            o.EnableDeterministicCoverageCritic,
            o.EnableFinalModelNormalizationPass,
            o.EnableFindingEvidenceGate,
            o.UseIncrementalReviewMode,
            deepSeek.Enabled,
            deepSeek.UseAsPrimaryReviewer);
    }

    private Task<ExternalReviewArtifact>? StartExternalReviewAsync(
        ReviewRun run,
        ExternalReviewInput input,
        CancellationToken cancellationToken)
    {
        var opts = externalReviewOptions.Value;
        if (!opts.Enabled)
        {
            return null;
        }

        logger.LogInformation(
            "Starting external review sidecar for run {RunId}: engine={EngineName}, commands={Commands}, baseUrl={BaseUrl}, inputMode={InputMode}, diffChars={DiffChars}",
            run.Id,
            opts.EngineName,
            string.Join(",", opts.Commands),
            opts.BaseUrl,
            opts.InputMode,
            input.DiffText.Length);

        var task = externalReviewEngine.RunAsync(run, input, cancellationToken);
        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Exception is not null)
                {
                    logger.LogWarning(
                        completedTask.Exception,
                        "External review sidecar task faulted for run {RunId}",
                        run.Id);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return task;
    }

    private static ExternalReviewInput CopyReviewInputForPrimaryDiff(
        ExternalReviewInput input,
        PreprocessedDiff reviewPreprocessed)
    {
        return new ExternalReviewInput
        {
            DiffText = reviewPreprocessed.FilteredDiffText,
            ChangedFiles = reviewPreprocessed.ChangedFiles,
            RiskDomainsSummary = ReviewRiskDomainFormatter.BuildAllRiskDomainsBlock(reviewPreprocessed.RiskDomains),
            RepositoryName = input.RepositoryName,
            ServiceName = input.ServiceName,
            PullRequestTitle = input.PullRequestTitle,
            PullRequestUrl = input.PullRequestUrl,
            RepositoryPath = input.RepositoryPath,
            RepositoryRemoteUrl = input.RepositoryRemoteUrl,
            GitFetchSourceRef = input.GitFetchSourceRef,
            GitFetchTargetRef = input.GitFetchTargetRef,
            GitHttpExtraHeader = input.GitHttpExtraHeader,
            SourceRef = input.SourceRef,
            TargetRef = input.TargetRef
        };
    }

    private async Task<IReadOnlyList<string>> RunPrimaryModelReviewAsync(
        string reviewDescription,
        PreprocessedDiff reviewPreprocessed,
        DiffAcquisitionResult diffResult,
        ReviewExecutionRequest request,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        return reviewPipelineOptions.Value.SinglePassFullDiffAndGraphPrimaryReview
            ? await ReviewSinglePassFullContextAsync(reviewDescription, reviewPreprocessed, diffResult, request, run, cancellationToken)
            : await ReviewChunksAsync(reviewDescription, reviewPreprocessed.ReviewChunks, diffResult, request, run, cancellationToken);
    }

    private async Task<DeepSeekTuiReviewResult> TryRunDeepSeekTuiReviewAsync(
        ReviewRun run,
        ExternalReviewInput input,
        CancellationToken cancellationToken)
    {
        var opts = deepSeekTuiReviewOptions.Value;
        if (!opts.Enabled)
        {
            return DeepSeekTuiReviewResult.Empty;
        }

        await PersistAndPublishAsync(
            run,
            $"Ход выполнения пайплайна: {opts.EngineName} анализирует workspace",
            ReviewPipelineStage.ChunkReview,
            50,
            cancellationToken);

        using var progressGate = new SemaphoreSlim(1, 1);
        var lastProgressPercent = 50;

        async ValueTask PublishDeepSeekProgressAsync(DeepSeekTuiReviewProgress progress, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(progress.Message))
            {
                return;
            }

            await progressGate.WaitAsync(token);
            try
            {
                var nextPercent = Math.Clamp(Math.Max(progress.ProgressPercent, lastProgressPercent), 51, 70);
                lastProgressPercent = nextPercent;
                await PersistAndPublishAsync(
                    run,
                    progress.Message,
                    ReviewPipelineStage.ChunkReview,
                    nextPercent,
                    token);
            }
            finally
            {
                progressGate.Release();
            }
        }

        var result = await deepSeekTuiReviewEngine.RunAsync(
            run,
            input,
            cancellationToken,
            PublishDeepSeekProgressAsync);
        if (result.Succeeded)
        {
            logger.LogInformation(
                "DeepSeek-TUI review succeeded for run {RunId}: findings={Findings}, opportunities={Opportunities}, elapsedMs={ElapsedMs}, workspace={Workspace}",
                run.Id,
                result.Findings.Count,
                result.Opportunities.Count,
                result.ElapsedMilliseconds,
                result.WorkspacePath);
            return result;
        }

        logger.LogWarning(
            "DeepSeek-TUI review was not used for run {RunId}: status={Status}, attempted={Attempted}, message={Message}, workspace={Workspace}",
            run.Id,
            result.Status,
            result.Attempted,
            result.Message,
            result.WorkspacePath);
        var fallbackMessage = result.TimedOut || result.Status == "timed_out"
            ? $"{opts.EngineName} превысил таймаут {opts.TimeoutSeconds} с, используем основной пайплайн"
            : $"{opts.EngineName} не вернул структурированное ревью, используем основной пайплайн";
        await PersistAndPublishAsync(
            run,
            fallbackMessage,
            ReviewPipelineStage.ChunkReview,
            55,
            cancellationToken);
        return result;
    }

    private async Task<ExternalReviewArtifact> CompleteExternalReviewAsync(
        Task<ExternalReviewArtifact>? externalReviewTask,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        if (externalReviewTask is null)
        {
            return ExternalReviewArtifact.Empty;
        }

        var waitSeconds = Math.Max(0, externalReviewOptions.Value.MaxWaitAtEndSeconds);
        var completedTask = externalReviewTask.IsCompleted
            ? externalReviewTask
            : await Task.WhenAny(
                externalReviewTask,
                Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken));

        if (completedTask == externalReviewTask)
        {
            var artifact = await externalReviewTask;
            logger.LogInformation(
                "External review sidecar completed for run {RunId}: status={Status}, commands={Commands}, elapsedMs={ElapsedMs}",
                run.Id,
                artifact.Status,
                artifact.Commands.Count,
                artifact.ElapsedMilliseconds);
            return artifact;
        }

        logger.LogInformation(
            "External review sidecar is still running for run {RunId} after final wait of {WaitSeconds}s",
            run.Id,
            waitSeconds);
        return new ExternalReviewArtifact
        {
            Enabled = true,
            Attempted = true,
            EngineName = externalReviewOptions.Value.EngineName,
            Status = "running",
            Message = $"External review sidecar did not finish before final synthesis wait ({waitSeconds}s)."
        };
    }

    private async Task<ChangeSummaryResult?> TryBuildExternalChangeSummaryAsync(
        Task<ExternalReviewArtifact>? externalReviewTask,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var opts = externalReviewOptions.Value;
        if (externalReviewTask is null || !opts.UseChangeSummary)
        {
            return null;
        }

        var waitSeconds = Math.Max(0, opts.ChangeSummaryWaitSeconds);
        await PersistAndPublishAsync(
            run,
            $"Ждем описание изменений от {opts.EngineName}",
            ReviewPipelineStage.ChangeDescription,
            30,
            cancellationToken);

        var completedTask = externalReviewTask.IsCompleted
            ? externalReviewTask
            : await Task.WhenAny(
                externalReviewTask,
                Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken));
        if (completedTask != externalReviewTask)
        {
            logger.LogInformation(
                "External review sidecar did not provide change summary for run {RunId} within {WaitSeconds}s; falling back to primary model",
                run.Id,
                waitSeconds);
            return null;
        }

        var artifact = await externalReviewTask;
        var changeSummary = externalReviewArtifactParser.Parse(artifact).ChangeSummary;
        if (changeSummary is null)
        {
            logger.LogInformation(
                "External review sidecar completed without parseable change summary for run {RunId}; falling back to primary model",
                run.Id);
            return null;
        }

        logger.LogInformation(
            "Using external review sidecar change summary for run {RunId}: engine={EngineName}, hasDiagram={HasDiagram}",
            run.Id,
            artifact.EngineName,
            !string.IsNullOrWhiteSpace(changeSummary.DiagramMermaid));
        return changeSummary;
    }

    private async Task TrackExternalReviewCompletionAsync(
        Task<ExternalReviewArtifact> externalReviewTask,
        ReviewRun run)
    {
        try
        {
            var artifact = await externalReviewTask;
            run.UpdateArtifacts(CopyArtifactsWithSemanticCodeContext(
                run.Artifacts,
                run.Artifacts.SemanticCodeContext,
                artifact));
            await reviewRunRepository.UpdateAsync(run, CancellationToken.None);
            logger.LogInformation(
                "External review sidecar artifact persisted for run {RunId}: status={Status}, commands={Commands}, elapsedMs={ElapsedMs}",
                run.Id,
                artifact.Status,
                artifact.Commands.Count,
                artifact.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "External review sidecar artifact tracking failed for run {RunId}", run.Id);
        }
    }

    private async Task<string> CompleteWithProgressHeartbeatAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        ReviewRun run,
        ReviewPipelineStage stage,
        int progressPercent,
        string heartbeatMessage,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var completionTask = llmCompletionService.CompleteAsync(profile, request, cancellationToken);

        while (true)
        {
            var delayTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, delayTask);
            if (completedTask == completionTask)
            {
                return await completionTask;
            }

            var elapsedSeconds = (int)Math.Max(1, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            await PersistAndPublishAsync(
                run,
                $"{heartbeatMessage} ({elapsedSeconds} с)",
                stage,
                progressPercent,
                cancellationToken);
        }
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

        var response = await CompleteWithProgressHeartbeatAsync(
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
            run,
            ReviewPipelineStage.ChangeDescription,
            30,
            "Генерируем описание изменений: модель отвечает",
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

    private async Task<ExternalScoutReviewResult?> TryReviewExternalScoutMissingCriticsAsync(
        string description,
        PreprocessedDiff preprocessed,
        Task<ExternalReviewArtifact>? externalReviewTask,
        ReviewExecutionRequest request,
        ReviewRun run,
        IReadOnlyList<ReviewFinding> baselineKnownFindings,
        CancellationToken cancellationToken)
    {
        var pipelineOptions = reviewPipelineOptions.Value;
        if (!pipelineOptions.UseExternalReviewScoutMode ||
            externalReviewTask is null ||
            !externalReviewOptions.Value.Enabled ||
            !externalReviewOptions.Value.UseReviewFindings)
        {
            return null;
        }

        var externalReview = await CompleteExternalReviewAsync(externalReviewTask, run, cancellationToken);
        if (!externalReview.Attempted ||
            externalReview.Commands.Count == 0 ||
            externalReview.Status is "failed" or "timed_out")
        {
            logger.LogInformation(
                "External review scout mode skipped for run {RunId}: status={Status}, commands={Commands}",
                run.Id,
                externalReview.Status,
                externalReview.Commands.Count);
            return null;
        }

        var externalInsights = externalReviewArtifactParser.Parse(externalReview);
        var knownFindings = baselineKnownFindings.Count == 0
            ? externalInsights.Findings
            : externalInsights.Findings.Concat(baselineKnownFindings).ToArray();
        var critics = ExternalScoutCritics
            .Select(critic => (Critic: critic, DiffContext: BuildExternalScoutCriticDiffContext(preprocessed, critic)))
            .Where(item => !string.IsNullOrWhiteSpace(item.DiffContext))
            .ToArray();
        if (critics.Length == 0)
        {
            logger.LogInformation("External review scout mode had no critic diff context for run {RunId}", run.Id);
            return null;
        }

        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            request.ProviderProfileId,
            request.StageOverrides,
            cancellationToken);

        await PersistAndPublishAsync(
            run,
            $"PR-Agent scout готов: запускаем {critics.Length} missing-critics",
            ReviewPipelineStage.ChunkReview,
            50,
            cancellationToken);

        var responses = new string[critics.Length];
        var completedCritics = 0;
        var maxConcurrency = Math.Clamp(
            pipelineOptions.MaxConcurrentExternalReviewScoutCritics,
            1,
            critics.Length);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = critics.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                responses[index] = await llmCompletionService.CompleteAsync(
                    selection.Profile,
                    new LlmChatRequest
                    {
                        Model = selection.Model,
                        Temperature = 0,
                        ExpectJson = true,
                        SystemPrompt = BuildExternalScoutCriticSystemPrompt(item.Critic),
                        UserPrompt = BuildExternalScoutCriticUserPrompt(
                            description,
                            knownFindings,
                            item.Critic,
                            item.DiffContext)
                    },
                    cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }

            var completed = Interlocked.Increment(ref completedCritics);
            var progress = 50 + (int)Math.Round((completed / (double)critics.Length) * 22d);
            await PersistAndPublishAsync(
                run,
                $"Missing-critics: завершено {completed} из {critics.Length}",
                ReviewPipelineStage.ChunkReview,
                progress,
                cancellationToken);
        }).ToArray();

        await Task.WhenAll(tasks);
        logger.LogInformation(
            "External review scout mode completed for run {RunId}: critics={Critics}, externalFindings={ExternalFindings}",
            run.Id,
            critics.Length,
            externalInsights.Findings.Count);

        return new ExternalScoutReviewResult(
            responses.Where(response => !string.IsNullOrWhiteSpace(response)).ToArray(),
            externalReview,
            externalInsights);
    }

    private string BuildExternalScoutCriticDiffContext(
        PreprocessedDiff preprocessed,
        ExternalScoutCritic critic)
    {
        var maxCharacters = Math.Max(8000, reviewPipelineOptions.Value.ExternalReviewScoutMaxCriticCharacters);
        var chunks = preprocessed.ReviewChunks.Count > 0
            ? preprocessed.ReviewChunks
            : preprocessed.Chunks;
        var selectedChunks = chunks
            .Where(chunk => critic.Keywords.Any(keyword => chunk.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Take(8)
            .ToList();
        if (selectedChunks.Count == 0)
        {
            selectedChunks = chunks.Take(6).ToList();
        }

        var builder = new StringBuilder();
        foreach (var chunk in selectedChunks)
        {
            if (builder.Length >= maxCharacters)
            {
                break;
            }

            var remaining = maxCharacters - builder.Length;
            var text = chunk.Length > remaining
                ? chunk[..remaining]
                : chunk;
            builder.AppendLine(text);
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string BuildExternalScoutCriticSystemPrompt(ExternalScoutCritic critic)
    {
        return $$"""
            You are a focused missing-issues critic in an automated code review pipeline.
            PR-Agent has already produced a first review. Your task is to find only important issues that are missing from the known findings.

            Focus area:
            {{critic.Name}}

            Focus instructions:
            {{critic.Instructions}}

            Rules:
            - Do not repeat, rephrase, or split known findings.
            - Report only issues directly supported by the provided diff context.
            - Prefer High/Medium findings. Use Low only for a concrete test or maintainability trap.
            - Before reporting a missing call, missing wiring, or missing enrichment step, verify the provided diff context does not already show the exact call or equivalent wiring.
            - Put non-blocking maintainability, duplication, refactoring, and readability improvements in opportunities instead of findings unless the repetition creates a concrete bug.
            - Do not omit useful opportunities just because findings are also present.
            - Return every human-readable field in Russian: title, description, suggestion, and line_hint.
            - Keep file paths, identifiers, method names, type names, and code snippets unchanged.
            - If there are no new issues, return empty arrays.
            - Return valid JSON only, no markdown.

            JSON schema:
            {
              "findings": [
                {
                  "file": "path/from/diff",
                  "line_hint": "short location",
                  "type": "Security|Performance|Architecture|Bug|Reliability|Logic|CodeStyle",
                  "severity": "Critical|High|Medium|Low",
                  "title": "short title",
                  "description": "what breaks, when, and why",
                  "existing_code": "small concrete snippet from the diff",
                  "suggestion": "specific fix",
                  "start_line": 0,
                  "end_line": 0
                }
              ],
              "opportunities": [
                {
                  "file": "path/from/diff",
                  "line_hint": "short location",
                  "title": "short title",
                  "description": "non-blocking improvement and why it matters",
                  "suggestion": "specific improvement",
                  "start_line": 0,
                  "end_line": 0
                }
              ]
            }
            """;
    }

    private static string BuildExternalScoutCriticUserPrompt(
        string description,
        IReadOnlyList<ReviewFinding> knownFindings,
        ExternalScoutCritic critic,
        string diffContext)
    {
        var knownFindingsJson = JsonSerializer.Serialize(
            knownFindings.Take(20).Select(finding => new
            {
                finding.File,
                finding.LineHint,
                finding.Title,
                finding.Description,
                finding.StartLine,
                finding.EndLine
            }),
            new JsonSerializerOptions { WriteIndented = true });

        return $$"""
            Change description:
            {{description}}

            Known findings from PR-Agent. Do not repeat these:
            {{knownFindingsJson}}

            Critic focus:
            {{critic.Name}}

            Diff context:
            {{diffContext}}
            """;
    }

    private static readonly ExternalScoutCritic[] ExternalScoutCritics =
    [
        new(
            "SQL/data integrity",
            [
                ".sql",
                "select ",
                "join ",
                "where ",
                "group by",
                "first(",
                "isbasic",
                "isbcallowed",
                "replicbranch",
                "macroregion"
            ],
            "Look for changed SQL semantics, removed business filters, row multiplication, non-deterministic picks, null handling, or data-integrity changes that PR-Agent missed."),
        new(
            "Concurrency/cache/background services",
            [
                "cache",
                "volatile",
                "lock",
                "semaphore",
                "concurrent",
                "backgroundservice",
                "hostedservice",
                "refreshasync",
                "dictionary"
            ],
            "Look for races, inconsistent cache publication, background-service lifetime issues, partial refresh visibility, cancellation handling, and stale/error state behavior."),
        new(
            "DI/options/config/bootstrap",
            [
                "adddependencies",
                "configure<",
                "options",
                "appsettings",
                "validateonstart",
                "hostedservice",
                "singleton",
                "scoped"
            ],
            "Look for wrong config section binding, missing validation, unsafe service lifetimes, startup failure modes, or options defaults that can silently change production behavior."),
        new(
            "Tests/seeds/regression coverage",
            [
                "tests",
                "testcontainer",
                "seed",
                "assert",
                "fact]",
                "theory]",
                "task.delay"
            ],
            "Look for tests that pass for the wrong reason, missing seed data, brittle timing, unasserted behavior, or coverage gaps around changed business rules."),
        new(
            "Public contracts/nullability/serialization",
            [
                "notmapped",
                "nullable",
                "string ",
                "int?",
                "readmodel",
                "dto",
                "excel",
                "return null",
                "json"
            ],
            "Look for public/read-model contract drift, nullable mismatches, serialization/export fields populated later than consumers expect, and null-return contracts. Do not report missing enrichment when the diff context already shows an EnrichWithRegionData or equivalent call before mapping/export."),
        new(
            "Maintainability/duplication",
            [
                "extension",
                "enrich",
                "enrichwith",
                "foreach",
                "orderregion",
                "macroregion",
                "timezone",
                "regioncacheextensions",
                "mapper",
                "provider"
            ],
            "Look specifically for repeated changed logic across overloads, helpers, mapping/enrichment code, and call sites that is likely to drift. Return high-signal duplication as opportunities when a small shared helper or strategy would reduce future mistakes. Use findings only when the duplicate logic already diverges in a way that breaks visible behavior.")
    ];

    private sealed record ExternalScoutCritic(
        string Name,
        IReadOnlyList<string> Keywords,
        string Instructions);

    private sealed record ExternalScoutReviewResult(
        IReadOnlyList<string> RawResponses,
        ExternalReviewArtifact ExternalReview,
        ExternalReviewInsights Insights);

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
        var semanticCodeContextResult = await reviewCodeSemanticContextService.BuildContextAsync(
            run.Id,
            diffResult,
            preprocessed,
            cancellationToken);
        run.UpdateArtifacts(CopyArtifactsWithSemanticCodeContext(
            run.Artifacts,
            semanticCodeContextResult.Diagnostics));
        await reviewRunRepository.UpdateAsync(run, cancellationToken);

        var semanticCodeContext = semanticCodeContextResult.Snippets;
        var payload = BuildSinglePassDiffAndGraphPayload(
            preprocessed,
            deterministicContext,
            semanticCodeContext);
        logger.LogInformation(
            "Full-context primary review for run {RunId}: payloadChars={PayloadChars}, reviewHints={ReviewHints}, deterministicToolResponses={ToolResponses}, semanticCodeSnippets={SemanticCodeSnippets}, semanticCodeStatus={SemanticCodeStatus}, semanticCodeCache={SourceCacheHit}/{TargetCacheHit}",
            run.Id,
            payload.Length,
            preprocessed.ReviewHints.Count,
            deterministicContext.Count,
            semanticCodeContext.Count,
            semanticCodeContextResult.Diagnostics.Status,
            semanticCodeContextResult.Diagnostics.SourceCacheHit,
            semanticCodeContextResult.Diagnostics.TargetCacheHit);

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
        var maxIterations = Math.Clamp(reviewPipelineOptions.Value.FullContextMaxToolIterations, 0, 6);

        var response = await CompleteWithProgressHeartbeatAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = selection.Temperature,
                ExpectJson = true,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt
            },
            run,
            ReviewPipelineStage.ChunkReview,
            52,
            "Первичное ревью: модель анализирует полный diff",
            cancellationToken);

        var currentPayload = userPrompt;
        if (maxIterations == 0)
        {
            logger.LogInformation(
                "Full-context tool loop is disabled for run {RunId}: FullContextMaxToolIterations=0",
                run.Id);
        }

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var envelope = ParseChunkReviewAgentEnvelope(response);
            LogFullContextToolRequestDecision(run, iteration, envelope);
            if (!envelope.NeedMoreContext || envelope.ToolRequests.Count == 0)
            {
                break;
            }

            var workspaceToolResponses = await reviewWorkspaceToolExecutor.ExecuteAsync(
                diffResult,
                "(full-diff)",
                envelope.ToolRequests,
                cancellationToken);
            LogFullContextToolResponses(run, iteration, workspaceToolResponses, "workspace");

            var fallbackToolResponses = DiffToolFallbackContextBuilder.Build(
                preprocessed.FilteredDiffText,
                envelope.ToolRequests,
                workspaceToolResponses);
            LogFullContextToolResponses(run, iteration, fallbackToolResponses, "diff-fallback");

            var toolResponses = workspaceToolResponses
                .Concat(fallbackToolResponses)
                .ToArray();
            if (toolResponses.Length == 0)
            {
                logger.LogInformation(
                    "Full-context tool loop produced no results for run {RunId}, iteration {Iteration}",
                    run.Id,
                    iteration);
                break;
            }

            currentPayload = BuildChunkReviewPayload(currentPayload, toolResponses);
            response = await CompleteWithProgressHeartbeatAsync(
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
                run,
                ReviewPipelineStage.ChunkReview,
                62,
                "Первичное ревью: модель проверяет tool-context",
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

        if (!reviewPipelineOptions.Value.EnableDeterministicCoverageCritic)
        {
            logger.LogInformation(
                "Deterministic coverage critic is disabled for run {RunId}: reviewHints={ReviewHints}, deterministicContext={DeterministicContext}",
                run.Id,
                preprocessed.ReviewHints.Count,
                deterministicContext.Count);
            return null;
        }

        await PersistAndPublishAsync(
            run,
            "Проверяем покрытие deterministic review-hints",
            ReviewPipelineStage.ChunkReview,
            68,
            cancellationToken);

        var hintsBlock = JoinNonEmptyBlocks(
            ReviewRiskDomainFormatter.BuildAllRiskDomainsBlock(preprocessed.RiskDomains),
            ReviewHintFormatter.BuildAllHintsBlock(preprocessed.ReviewHints));
        var contextBlock = BuildDeterministicContextBlock(deterministicContext);
        var response = await CompleteWithProgressHeartbeatAsync(
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
            run,
            ReviewPipelineStage.ChunkReview,
            68,
            "Проверяем покрытие deterministic review-hints: модель отвечает",
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
        IReadOnlyList<ReviewWorkspaceToolResponse> deterministicContext,
        IReadOnlyList<ReviewWorkspaceToolResponse> semanticCodeContext)
    {
        var sb = new StringBuilder();
        var riskDomainsBlock = ReviewRiskDomainFormatter.BuildAllRiskDomainsBlock(preprocessed.RiskDomains);
        if (!string.IsNullOrWhiteSpace(riskDomainsBlock))
        {
            sb.AppendLine(riskDomainsBlock);
            sb.AppendLine();
        }

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

        var semanticCodeContextBlock = BuildSemanticCodeContextBlock(semanticCodeContext);
        if (!string.IsNullOrWhiteSpace(semanticCodeContextBlock))
        {
            sb.AppendLine(semanticCodeContextBlock);
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

    private static string BuildSemanticCodeContextBlock(IReadOnlyList<ReviewWorkspaceToolResponse> semanticCodeContext)
    {
        if (semanticCodeContext.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("=== SEMANTIC SOURCE/TARGET CODE CONTEXT (top retrieved snippets; supporting evidence, not changed diff) ===");
        foreach (var response in semanticCodeContext)
        {
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
            builder.AppendLine(TrimForPrompt(response.Content, 6000));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
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

    private static string JoinNonEmptyBlocks(params string[] blocks)
    {
        return string.Join(
            "\n\n",
            blocks.Where(block => !string.IsNullOrWhiteSpace(block)).Select(block => block.Trim()));
    }

    internal static IReadOnlyList<ReviewFinding> BuildConfirmedDeterministicFindings(
        IReadOnlyList<ReviewHint> hints,
        IReadOnlyList<ReviewFinding> existingFindings,
        bool externalPrimarySucceeded = false)
    {
        return hints
            .Where(hint => !externalPrimarySucceeded || IsHighSignalDeterministicSupplement(hint))
            .Select(BuildConfirmedDeterministicFinding)
            .Where(finding => finding is not null)
            .Cast<ReviewFinding>()
            .Where(finding => existingFindings.All(existing => !CoversFinding(existing, finding)))
            .ToArray();
    }

    private static bool IsHighSignalDeterministicSupplement(ReviewHint hint)
    {
        return hint.RuleId switch
        {
            "OPTIONS_BOUND_WITHOUT_VALIDATION" => false,
            "NON_NULLABLE_CONTRACT_RETURNS_NULL" => false,
            _ => true
        };
    }

    private static ReviewFinding? BuildConfirmedDeterministicFinding(ReviewHint hint)
    {
        return hint.RuleId switch
        {
            "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING" => BuildKafkaCachePublishFinding(hint),
            "SQL_REMOVED_BUSINESS_FILTER" => BuildSqlRemovedBusinessFilterFinding(hint),
            "SQL_JOIN_ALIAS_ONLY_USED_IN_JOIN" => BuildSqlUnusedJoinAliasFinding(hint),
            "OPTIONS_SECTION_NOT_VISIBLE_IN_CHANGED_CONFIG" => BuildOptionsSectionMismatchFinding(hint),
            "OPTIONS_BOUND_WITHOUT_VALIDATION" => BuildOptionsWithoutValidationFinding(hint),
            "GROUP_BY_FIRST_WITHOUT_ORDER" => BuildGroupByFirstWithoutOrderFinding(hint),
            "NON_NULLABLE_CONTRACT_RETURNS_NULL" => BuildNonNullableContractReturnsNullFinding(hint),
            "API_UNBOUNDED_PAGE_SIZE" => BuildUnboundedPageSizeFinding(hint),
            "EF_BULK_UPDATE_THEN_INSERT_WITHOUT_TRANSACTION" => BuildNonAtomicBulkUpdateFinding(hint),
            "JWT_UTC_COMPARED_WITH_LOCAL_TIME" => BuildJwtUtcComparedWithLocalTimeFinding(hint),
            "TASK_FACTORY_STARTNEW_ASYNC_IO_TOKEN_FLOW" => BuildTaskFactoryStartNewAsyncIoTokenFlowFinding(hint),
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

    private static ReviewFinding BuildSqlRemovedBusinessFilterFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "SQL business filters",
            FindingCategory.Logic,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "SQL перестал фильтровать разрешённые регионы",
            "В SQL удалены предикаты `IsBasic`/`IsBcAllowed`, а новый запрос не показывает эквивалентного ограничения. Если в ReplicBranch есть небазовые или запрещённые для broadband регионы, список заказов начнёт возвращать записи, которые раньше отсекались на уровне запроса.",
            hint.Evidence,
            "Вернуть бизнес-фильтр в SQL либо явно перенести его в новый источник данных кэша и покрыть сценарий тестом: регион с IsBasic=false или IsBcAllowed=false не должен попадать в результат.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildSqlUnusedJoinAliasFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "SQL join",
            FindingCategory.Logic,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Неиспользуемый join может размножить строки",
            "После удаления предикатов alias из join больше не используется вне самого join-блока. Если присоединённая таблица содержит несколько строк на ключ, такой left join не фильтрует результат, но может продублировать заказы.",
            hint.Evidence,
            "Удалить неиспользуемый join или оставить только тот join/predicate, который действительно нужен для фильтрации. Для таблицы с потенциальными дублями добавить детерминирующее условие или EXISTS.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildOptionsSectionMismatchFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "options binding",
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Options привязаны к невидимой секции конфига",
            "DI привязывает options к секции, которой нет среди изменённых appsettings-секций. Если ключи фактически лежат в другой секции, runtime будет использовать дефолты, а environment overrides для добавленного параметра не сработают.",
            hint.Evidence,
            "Согласовать имя секции в `Configure<TOptions>` с appsettings/environment convention или вынести `SectionName` в options-класс и использовать его в DI и конфиге.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildOptionsWithoutValidationFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "options validation",
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Options регистрируются без startup-валидации",
            "Новая конфигурация привязана через options, но рядом не видно `Validate`/`ValidateOnStart`. Если обязательное значение отсутствует или лежит в неправильной секции, сервис узнает об этом только при первом использовании фонового сервиса или запроса.",
            hint.Evidence,
            "Добавить валидацию options при старте или явно обработать безопасные дефолты там, где параметр используется.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildGroupByFirstWithoutOrderFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "GroupBy/First",
            FindingCategory.Logic,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "GroupBy выбирает первый регион недетерминированно",
            "Код группирует записи и берёт `First()` без явного порядка или критерия выбора. Если источник вернёт несколько строк на один код региона, в кэш попадёт произвольная запись, и название/часовой пояс/макрорегион могут зависеть от порядка ответа БД.",
            hint.Evidence,
            "Выбрать каноническую запись явным predicate/OrderBy, например предпочитать IsBasic/IsBcAllowed или другой бизнес-признак, и покрыть дубль по RegionCode тестом.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildNonNullableContractReturnsNullFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "nullable contract",
            FindingCategory.Bug,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Ненулевой контракт возвращает null",
            "Метод с non-nullable сигнатурой рядом с `return null` вводит вызывающий код в заблуждение: потребители могут считать значение обязательным, а при отсутствии результата получить null в модель, HTTP-заголовок или другой downstream-контракт и словить NRE/некорректный запрос после последующей обработки.",
            hint.Evidence,
            "Сделать возвращаемый тип nullable в интерфейсе и реализации либо не возвращать null: вернуть безопасное значение или явную ошибку, если отсутствие результата является исключительным состоянием.",
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

    private static ReviewFinding BuildJwtUtcComparedWithLocalTimeFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "JWT token time",
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Refresh JWT-токена сравнивает UTC-время с локальным временем",
            "JWT-поля `ValidTo`/`ValidFrom` и claim `exp` интерпретируются как UTC, а изменённая логика refresh сравнивает их с `DateTime.Now`/`DateTimeOffset.Now`. На сервере с локальной timezone не UTC это сдвигает момент обновления токена: сервис может слишком рано дёргать identity либо, наоборот, продолжать использовать почти истёкший токен.",
            hint.Evidence,
            "Сравнивать JWT-время с `DateTime.UtcNow`/`DateTimeOffset.UtcNow` и покрыть refresh тестом с токеном, у которого `ValidTo` задан в UTC.",
            hint.StartLine,
            hint.StartLine);
    }

    private static ReviewFinding BuildTaskFactoryStartNewAsyncIoTokenFlowFinding(ReviewHint hint)
    {
        return new ReviewFinding(
            hint.FilePath,
            hint.StartLine > 0 ? $"line {hint.StartLine}" : "Task.Factory.StartNew",
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            ReviewFindingSource.InitialReview,
            "Task.Factory.StartNew используется для async I/O refresh токена",
            "В diff виден `Task.Factory.StartNew(...).Unwrap()` для async factory, а тот же flow используется для получения/refresh токена через HTTP. `StartNew`/`Task.Run` здесь не CPU-bound offload: он добавляет лишнее планирование в thread pool, может потерять cancellation/lifetime семантику и вместе с fire-and-forget refresh оставляет исключения обновления токена без явного наблюдения.",
            hint.Evidence,
            "Для async factory передавать фабрику напрямую (`base(taskFactory)`) или сделать отдельный async path без `StartNew`. Для фонового refresh явно наблюдать задачу и логировать/обрабатывать fault, а cancellation token протащить до HTTP-запроса токена.",
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

    internal static IReadOnlyList<ReviewFinding> RestoreDroppedDistinctFindings(
        IReadOnlyList<ReviewFinding> originalFindings,
        IReadOnlyList<ReviewFinding> normalizedFindings)
    {
        if (originalFindings.Count == 0)
        {
            return DeduplicateFindings(normalizedFindings);
        }

        var restored = DeduplicateFindings(normalizedFindings).ToList();
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
                restored[coveringIndex] = ChoosePreferredFinding(restored[coveringIndex], candidate);

                continue;
            }

            restored.Add(candidate);
        }

        return DeduplicateFindings(restored);
    }

    internal static IReadOnlyList<ReviewFinding> DeduplicateFindings(
        IReadOnlyList<ReviewFinding> findings,
        bool preserveExternalReviewFindings = false)
    {
        if (findings.Count < 2)
        {
            return findings;
        }

        var deduplicated = new List<ReviewFinding>();
        foreach (var candidate in findings)
        {
            if (preserveExternalReviewFindings &&
                candidate.Source is ReviewFindingSource.ExternalReview)
            {
                deduplicated.Add(candidate);
                continue;
            }

            var duplicateIndex = deduplicated.FindIndex(existing => CoversFinding(existing, candidate));
            if (duplicateIndex >= 0)
            {
                if (preserveExternalReviewFindings &&
                    deduplicated[duplicateIndex].Source is ReviewFindingSource.ExternalReview)
                {
                    continue;
                }

                deduplicated[duplicateIndex] = ChoosePreferredFinding(deduplicated[duplicateIndex], candidate);
                continue;
            }

            deduplicated.Add(candidate);
        }

        return deduplicated;
    }

    private static ReviewFinding ChoosePreferredFinding(ReviewFinding existing, ReviewFinding candidate)
    {
        var existingRank = GetSeverityRank(existing.Severity);
        var candidateRank = GetSeverityRank(candidate.Severity);
        if (candidateRank < existingRank)
        {
            return candidate;
        }

        if (existingRank < candidateRank)
        {
            return existing;
        }

        var existingScore = ComputeFindingSpecificityScore(existing);
        var candidateScore = ComputeFindingSpecificityScore(candidate);
        return candidateScore > existingScore
            ? candidate
            : existing;
    }

    private static int ComputeFindingSpecificityScore(ReviewFinding finding)
    {
        var score = 0;
        if (finding.Source == ReviewFindingSource.ExternalReview)
        {
            score += 10;
        }

        if (finding.StartLine > 0)
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(finding.ExistingCode))
        {
            score += Math.Min(20, finding.ExistingCode.Length / 25);
        }

        var text = BuildFindingText(finding);
        if (text.Contains("return null", StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
        }

        if (text.Contains("validateonstart", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("getsection", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("isbasic", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("isbcallowed", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        if (ClassifyFinding(finding) == FindingFailureKind.TaskFactoryStartNewAsyncIo &&
            ContainsAny(text, "fire-and-forget", "cancellation", "unobserved", "refresh токена", "refresh токен"))
        {
            score += 12;
        }

        return score;
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
        var samePath = PathsMatch(existing.File, candidate.File);
        var existingKind = ClassifyFinding(existing);
        var candidateKind = ClassifyFinding(candidate);
        var sameKnownKind = existingKind != FindingFailureKind.Unknown &&
                            existingKind == candidateKind;

        if (!samePath)
        {
            return sameKnownKind &&
                   AllowsCrossFileCoverage(existingKind) &&
                   HaveSharedSemanticAnchor(existing, candidate);
        }

        if (LineRangesAreClose(existing, candidate))
        {
            if (sameKnownKind)
            {
                return true;
            }

            if (existingKind != FindingFailureKind.Unknown &&
                candidateKind != FindingFailureKind.Unknown)
            {
                return false;
            }

            return HaveMeaningfulExactCodeMatch(existing.ExistingCode, candidate.ExistingCode) ||
                   ComputeTextSimilarity(existing, candidate) >= 0.42d;
        }

        if (sameKnownKind && HaveSharedSemanticAnchor(existing, candidate))
        {
            return true;
        }

        var existingText = BuildFindingText(existing);
        var candidateTerms = ExtractSignificantTerms($"{candidate.Title} {candidate.Description}");
        if (candidateTerms.Count == 0)
        {
            return false;
        }

        if (existingKind != FindingFailureKind.Unknown &&
            candidateKind != FindingFailureKind.Unknown &&
            existingKind != candidateKind)
        {
            return false;
        }

        var matches = candidateTerms.Count(term =>
            existingText.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matches >= Math.Min(4, candidateTerms.Count) &&
               ComputeTextSimilarity(existing, candidate) >= 0.36d;
    }

    private static bool AllowsCrossFileCoverage(FindingFailureKind kind)
    {
        return kind is FindingFailureKind.NonNullableContractReturnsNull or
            FindingFailureKind.OptionsBindingSection or
            FindingFailureKind.SqlBusinessFilter or
            FindingFailureKind.RestoreBuildFailure;
    }

    internal static IReadOnlyList<ReviewFinding> ApplyFindingEvidenceGate(
        IReadOnlyList<ReviewFinding> findings,
        PreprocessedDiff preprocessed,
        bool preserveExternalReviewFindings = false)
    {
        if (findings.Count == 0 || string.IsNullOrWhiteSpace(preprocessed.FilteredDiffText))
        {
            return findings;
        }

        var diffEvidence = BuildDiffFindingEvidence(preprocessed.FilteredDiffText);
        if (diffEvidence.Count == 0)
        {
            return findings;
        }

        return findings
            .Where(finding =>
                (preserveExternalReviewFindings &&
                 finding.Source is ReviewFindingSource.ExternalReview) ||
                HasDiffEvidence(finding, diffEvidence))
            .ToArray();
    }

    private static IReadOnlyList<DiffFindingEvidence> BuildDiffFindingEvidence(string diffText)
    {
        return ParseDiffSections(diffText)
            .Select(section => new DiffFindingEvidence(
                section.FilePath,
                ExtractPatchEvidenceText(section.Patch),
                ParseNewLineHunkRanges(section.Patch)))
            .Where(evidence => !string.IsNullOrWhiteSpace(evidence.FilePath))
            .ToArray();
    }

    private static bool HasDiffEvidence(
        ReviewFinding finding,
        IReadOnlyList<DiffFindingEvidence> diffEvidence)
    {
        if (ClassifyFinding(finding) == FindingFailureKind.RestoreBuildFailure)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(finding.File))
        {
            return false;
        }

        var evidence = diffEvidence.FirstOrDefault(item => PathsMatch(item.FilePath, finding.File));
        if (evidence is null)
        {
            return false;
        }

        if (FindingLineTouchesChangedHunk(finding, evidence.HunkRanges))
        {
            return true;
        }

        if (HasExistingCodeInDiffPatch(finding, evidence.NormalizedPatchText))
        {
            return true;
        }

        return HasKnownSemanticAnchorInDiff(finding, evidence.SemanticAnchors) ||
               HasHighSignalKnownDiffKeyword(finding, evidence.PatchText);
    }

    private static bool HasHighSignalKnownDiffKeyword(ReviewFinding finding, string patchText)
    {
        var normalizedPatch = NormalizeMatchValue(patchText);
        return ClassifyFinding(finding) switch
        {
            FindingFailureKind.SqlBusinessFilter =>
                normalizedPatch.Contains("isbasic", StringComparison.Ordinal) ||
                normalizedPatch.Contains("isbcallowed", StringComparison.Ordinal),
            FindingFailureKind.SqlRowMultiplication =>
                normalizedPatch.Contains("join", StringComparison.Ordinal) &&
                normalizedPatch.Contains("replicbranch", StringComparison.Ordinal),
            FindingFailureKind.GroupByFirstWithoutOrder =>
                normalizedPatch.Contains("groupby", StringComparison.Ordinal) &&
                normalizedPatch.Contains("first", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool FindingLineTouchesChangedHunk(
        ReviewFinding finding,
        IReadOnlyList<DiffLineRange> hunkRanges)
    {
        if (finding.StartLine <= 0 || hunkRanges.Count == 0)
        {
            return false;
        }

        var start = finding.StartLine;
        var end = Math.Max(start, finding.EndLine);
        const int lineTolerance = 4;
        return hunkRanges.Any(range =>
            start <= range.End + lineTolerance &&
            range.Start - lineTolerance <= end);
    }

    private static bool HasExistingCodeInDiffPatch(ReviewFinding finding, string normalizedPatchText)
    {
        if (string.IsNullOrWhiteSpace(finding.ExistingCode) ||
            string.IsNullOrWhiteSpace(normalizedPatchText))
        {
            return false;
        }

        return ExtractCandidateEvidenceLines(finding.ExistingCode)
            .Select(NormalizeEvidenceText)
            .Where(candidate => candidate.Length >= 12)
            .Any(normalizedPatchText.Contains);
    }

    private static bool HasKnownSemanticAnchorInDiff(
        ReviewFinding finding,
        IReadOnlySet<string> diffAnchors)
    {
        if (ClassifyFinding(finding) == FindingFailureKind.Unknown || diffAnchors.Count == 0)
        {
            return false;
        }

        var findingAnchors = ExtractSemanticAnchors(finding);
        return findingAnchors.Count > 0 && findingAnchors.Overlaps(diffAnchors);
    }

    private static IReadOnlyList<string> ExtractCandidateEvidenceLines(string existingCode)
    {
        var lines = existingCode
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('+', '-', ' '))
            .Where(line => line.Length >= 8)
            .Where(line => !line.All(character => character is '{' or '}' or ';' or ','))
            .OrderByDescending(line => line.Length)
            .Take(6)
            .ToList();

        if (existingCode.Length <= 600)
        {
            lines.Add(existingCode);
        }

        return lines;
    }

    private static string ExtractPatchEvidenceText(string patch)
    {
        var builder = new StringBuilder();
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) ||
                line.StartsWith("-", StringComparison.Ordinal) ||
                line.StartsWith(" ", StringComparison.Ordinal))
            {
                builder.AppendLine(line[1..]);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<DiffLineRange> ParseNewLineHunkRanges(string patch)
    {
        var ranges = new List<DiffLineRange>();
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            var range = ParseNewLineRange(line);
            if (range is not null)
            {
                ranges.Add(range);
            }
        }

        return ranges;
    }

    private static DiffLineRange? ParseNewLineRange(string hunkHeader)
    {
        var plusIndex = hunkHeader.IndexOf('+');
        if (plusIndex < 0)
        {
            return null;
        }

        var endIndex = hunkHeader.IndexOf(' ', plusIndex);
        if (endIndex < 0)
        {
            endIndex = hunkHeader.IndexOf("@@", plusIndex, StringComparison.Ordinal);
        }

        if (endIndex < 0)
        {
            endIndex = hunkHeader.Length;
        }

        var segment = hunkHeader[plusIndex..endIndex].TrimStart('+');
        var parts = segment.Split(',', 2);
        if (!int.TryParse(parts[0], out var start))
        {
            return null;
        }

        var count = parts.Length == 2 && int.TryParse(parts[1], out var parsedCount)
            ? parsedCount
            : 1;
        var end = Math.Max(start, start + Math.Max(0, count - 1));
        return new DiffLineRange(start, end);
    }

    private static string NormalizeEvidenceText(string value)
    {
        return new string(value
            .Trim()
            .TrimStart('+', '-', ' ')
            .Where(character => !char.IsWhiteSpace(character))
            .ToArray())
            .ToLowerInvariant();
    }

    internal static IReadOnlyList<ReviewFinding> SuppressLowPrecisionFindings(
        IReadOnlyList<ReviewFinding> findings,
        bool preserveExternalReviewFindings = false)
    {
        if (findings.Count == 0)
        {
            return findings;
        }

        return findings
            .Where(finding =>
                (preserveExternalReviewFindings &&
                 finding.Source is ReviewFindingSource.ExternalReview) ||
                !IsLowPrecisionFinding(finding))
            .ToArray();
    }

    internal static IReadOnlyList<ReviewFinding> NormalizeFindingSeverities(
        IReadOnlyList<ReviewFinding> findings,
        bool preserveExternalReviewFindings = false)
    {
        if (findings.Count == 0)
        {
            return findings;
        }

        ReviewFinding[]? normalized = null;
        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            var shouldPreserveFinding = preserveExternalReviewFindings &&
                                        finding.Source is ReviewFindingSource.ExternalReview;
            var normalizedFinding = shouldPreserveFinding
                ? finding
                : NormalizeFindingSeverity(finding);
            if (normalizedFinding == finding && normalized is null)
            {
                continue;
            }

            normalized ??= findings.ToArray();
            normalized[index] = normalizedFinding;
        }

        return normalized ?? findings;
    }

    internal static IReadOnlyList<ReviewOpportunityItem> BuildOpportunitiesFromDemotableFindings(IReadOnlyList<ReviewFinding> findings)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        return findings
            .Where(IsDemotableTestMaintainabilityFinding)
            .Select(finding => new ReviewOpportunityItem(
                NormalizeOpportunityPath(finding.File),
                finding.LineHint,
                finding.Title,
                finding.Description,
                finding.Suggestion,
                finding.StartLine))
            .ToArray();
    }

    internal static IReadOnlyList<ReviewFinding> SuppressContradictedByDiffFindings(
        IReadOnlyList<ReviewFinding> findings,
        string diffText,
        bool preserveExternalReviewFindings = false)
    {
        if (findings.Count == 0 || string.IsNullOrWhiteSpace(diffText))
        {
            return findings;
        }

        return findings
            .Where(finding =>
                (preserveExternalReviewFindings &&
                 finding.Source is ReviewFindingSource.ExternalReview) ||
                !IsSingletonRegistrationSpeculationContradictedByDiff(finding, diffText))
            .ToArray();
    }

    internal static IReadOnlyList<ReviewOpportunityItem> SuppressLowSignalOpportunities(IReadOnlyList<ReviewOpportunityItem> opportunities)
    {
        if (opportunities.Count == 0)
        {
            return opportunities;
        }

        return opportunities
            .Where(opportunity => !IsLowSignalOpportunity(opportunity))
            .ToArray();
    }

    private static ReviewFinding NormalizeFindingSeverity(ReviewFinding finding)
    {
        if (ShouldDowngradeCriticalTestPackageInProductionProject(finding))
        {
            return finding with { Severity = FindingSeverity.High };
        }

        return finding;
    }

    private static bool ShouldDowngradeCriticalTestPackageInProductionProject(ReviewFinding finding)
    {
        if (finding.Severity != FindingSeverity.Critical ||
            !IsProjectFile(finding.File) ||
            IsTestOnlyLocation(finding.File))
        {
            return false;
        }

        var text = BuildFindingText(finding);
        return ContainsAny(text, "packagereference", "package reference", "пакет", "зависим") &&
               ContainsAny(
                   text,
                   "moq",
                   "mockqueryable",
                   "xunit",
                   "nunit",
                   "fluentassertions",
                   "shouldly",
                   "autofixture",
                   "coverlet",
                   "microsoft.net.test.sdk",
                   "test.sdk") &&
               ContainsAny(
                   text,
                   "production",
                   "prod",
                   "боев",
                   "продак",
                   "не тестов",
                   "production-проект",
                   "production-завис",
                   "production-сбор");
    }

    private static bool IsProjectFile(string file)
    {
        return NormalizePath(file).EndsWith(".csproj", StringComparison.Ordinal);
    }

    private static int CountSeverityChanges(
        IReadOnlyList<ReviewFinding> before,
        IReadOnlyList<ReviewFinding> after)
    {
        var count = Math.Min(before.Count, after.Count);
        var changes = 0;
        for (var index = 0; index < count; index++)
        {
            if (before[index].Severity != after[index].Severity)
            {
                changes++;
            }
        }

        return changes;
    }

    private static bool IsLowPrecisionFinding(ReviewFinding finding)
    {
        return IsDemotableTestMaintainabilityFinding(finding) ||
               IsSemaphoreSlimDisposeOnlyFinding(finding) ||
               IsLowSeverityCodeStyleFinding(finding) ||
               IsUiSchedulerSpeculationFinding(finding) ||
               IsTaskFactoryStartNewCleanupFinding(finding) ||
               IsReturnAfterExceptionWithoutCatchFinding(finding) ||
               IsHeaderDuplicateSpeculationFinding(finding) ||
               IsHttpClientFactorySharedInstanceRaceFinding(finding) ||
               IsHttpClientFactoryDefaultClientPolicyFinding(finding) ||
               IsDefensiveNullGuardOnlyFinding(finding) ||
               IsStyleOrDocumentationOnlyFinding(finding);
    }

    private static bool IsLowSeverityCodeStyleFinding(ReviewFinding finding)
        => finding.Category == FindingCategory.CodeStyle && finding.Severity == FindingSeverity.Low;

    private static bool IsDemotableTestMaintainabilityFinding(ReviewFinding finding)
    {
        if (!IsTestOnlyLocation(finding.File))
        {
            return false;
        }

        var text = BuildFindingText(finding);
        if (LooksLikeConcreteTestFailureOrBuildRisk(text))
        {
            return false;
        }

        return ContainsAny(
                   text,
                   "testasyncqueryprovider",
                   "testasyncenumerable",
                   "testasyncenumerator",
                   "mockqueryable",
                   "ручн",
                   "самопис",
                   "дублирован",
                   "унифиц",
                   "helper",
                   "вспомогатель",
                   "reflection",
                   "private method",
                   "private-метод",
                   "приватн",
                   "рефактор",
                   "поддержк",
                   "не покрыт",
                   "не покрывает",
                   "coverage") &&
               ContainsAny(
                   text,
                   "test",
                   "тест",
                   "xunit",
                   "moq",
                   "mock",
                   "assert",
                   "fact",
                   "theory",
                   "helper",
                   "reflection",
                   "private");
    }

    private static bool IsTestOnlyLocation(string file)
    {
        var normalized = NormalizePath(file);
        return normalized.Contains("/tests/", StringComparison.Ordinal) ||
               normalized.Contains(".tests/", StringComparison.Ordinal) ||
               normalized.Contains("testcontainers", StringComparison.Ordinal) ||
               normalized.EndsWith("tests.csproj", StringComparison.Ordinal) ||
               normalized.EndsWith("test.csproj", StringComparison.Ordinal);
    }

    private static bool LooksLikeConcreteTestFailureOrBuildRisk(string text)
    {
        return ContainsAny(
            text,
            "не компилиру",
            "не собира",
            "compile",
            "build",
            "restore",
            "nu110",
            "netsdk",
            "cs0",
            "падает",
            "не проходит",
            "assertionexception",
            "expected",
            "actual",
            "seed",
            "fixture",
            "task.delay",
            "thread.sleep",
            "flaky",
            "нестабильн",
            "race",
            "гонка");
    }

    private static bool AreEquivalentOpportunities(ReviewOpportunityItem existing, ReviewOpportunityItem candidate)
    {
        if (!NormalizePath(existing.File).Equals(NormalizePath(candidate.File), StringComparison.Ordinal))
        {
            return false;
        }

        var existingTitle = NormalizeMatchValue(existing.Title);
        var candidateTitle = NormalizeMatchValue(candidate.Title);
        if (existingTitle.Length > 0 &&
            existingTitle.Equals(candidateTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var existingText = NormalizeMatchValue($"{existing.Title} {existing.Description}");
        var candidateText = NormalizeMatchValue($"{candidate.Title} {candidate.Description}");
        return existingText.Length >= 24 &&
               candidateText.Length >= 24 &&
               (existingText.Contains(candidateText, StringComparison.Ordinal) ||
                candidateText.Contains(existingText, StringComparison.Ordinal));
    }

    private static string NormalizeOpportunityPath(string file)
    {
        return file.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static bool IsSingletonRegistrationSpeculationContradictedByDiff(ReviewFinding finding, string diffText)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "singleton", "синглтон") ||
            !ContainsAny(text, "регистрац", "registered", "lifetime") ||
            !ContainsAny(text, "если", "может", "should", "требуется", "нужно") ||
            !ContainsAny(text, "кэш", "cache", "token", "токен"))
        {
            return false;
        }

        var serviceNames = ExtractSemanticAnchors(finding)
            .Where(anchor => anchor.EndsWith("service", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (serviceNames.Length == 0)
        {
            return false;
        }

        return serviceNames.Any(serviceName =>
            Regex.IsMatch(
                diffText,
                $@"AddSingleton\s*<[^>\n\r]*\b{Regex.Escape(serviceName)}\b[^>\n\r]*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                diffText,
                $@"AddSingleton\s*\([^;\n\r]*\b{Regex.Escape(serviceName)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool IsHttpClientFactorySharedInstanceRaceFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "baseaddress") ||
            !ContainsAny(text, "createclient", "ihttpclientfactory") ||
            !ContainsAny(text, "гонка", "race", "потокобезопас", "thread-safe", "shared", "общий", "default"))
        {
            return false;
        }

        var code = NormalizeMatchValue(finding.ExistingCode);
        return code.Contains("createclient()", StringComparison.Ordinal) &&
               code.Contains("baseaddress", StringComparison.Ordinal) &&
               !code.Contains("static", StringComparison.Ordinal) &&
               !code.Contains("readonlyhttpclient", StringComparison.Ordinal);
    }

    private static bool IsHttpClientFactoryDefaultClientPolicyFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "createclient", "ihttpclientfactory") ||
            !ContainsAny(text, "именован", "named") ||
            !ContainsAny(text, "пул", "pool", "политик", "policy", "policies"))
        {
            return false;
        }

        var code = NormalizeMatchValue(finding.ExistingCode);
        return code.Contains("createclient()", StringComparison.Ordinal) &&
               !code.Contains("createclient(\"", StringComparison.Ordinal);
    }

    private static bool IsSemaphoreSlimDisposeOnlyFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        return ContainsAny(text, "semaphoreslim") &&
               ContainsAny(text, "dispose", "idisposable", "освобожда", "утечка ресурса") &&
               !ContainsAny(text, "availablewaithandle", "loop", "цикл", "каждую итерац", "многократн");
    }

    private static bool IsUiSchedulerSpeculationFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        return ContainsAny(text, "task.factory.startnew", "taskscheduler") &&
               ContainsAny(text, "ui-поток", "ui поток", "synchronizationcontext", "wpf", "winforms") &&
               ContainsAny(text, "может", "если", "контекст", "захват");
    }

    private static bool IsTaskFactoryStartNewCleanupFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        return LooksLikeTaskFactoryStartNewCleanup(text);
    }

    private static bool IsReturnAfterExceptionWithoutCatchFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "исключ", "exception", "сбое", "failure") ||
            !ContainsAny(text, "возврат", "возвращ", "return") ||
            !ContainsAny(text, "устаревш", "cached", "кэширован"))
        {
            return false;
        }

        var code = finding.ExistingCode;
        return code.Contains("try", StringComparison.OrdinalIgnoreCase) &&
               code.Contains("finally", StringComparison.OrdinalIgnoreCase) &&
               !code.Contains("catch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeaderDuplicateSpeculationFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        return ContainsAny(text, "headers.add", "request.headers.add", "повторном добавлении", "уже присутствует") &&
               ContainsAny(text, "correlationid", "custom header", "crmheadernames") &&
               ContainsAny(text, "invalidoperationexception", "исключ");
    }

    private static bool IsStyleOrDocumentationOnlyFinding(ReviewFinding finding)
    {
        if (ClassifyFinding(finding) != FindingFailureKind.Unknown)
        {
            return false;
        }

        var text = BuildFindingText(finding);
        return ContainsAny(
            text,
            "<returns>",
            "xml-тег",
            "xml tag",
            "неиспользуемый импорт",
            "unused using",
            "опечатк",
            "комментар",
            "переименовать параметр",
            "назван");
    }

    private static bool IsDefensiveNullGuardOnlyFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "не проверяет", "не проверяют", "missing null check", "argumentnullexception") ||
            !ContainsAny(text, "параметр", "parameter", "regioncacherepository"))
        {
            return false;
        }

        return finding.Severity == FindingSeverity.Low ||
               ContainsAny(text, "штатном режиме", "di гарантирует", "защитн", "диагност", "informative");
    }

    private static bool IsLowSignalOpportunity(ReviewOpportunityItem opportunity)
    {
        var text = $"{opportunity.File} {opportunity.LineHint} {opportunity.Title} {opportunity.Description} {opportunity.Suggestion}";
        if (LooksLikeTaskFactoryStartNewAsyncIoConcern(text))
        {
            return false;
        }

        if (IsHighSignalDuplicationOpportunity(text))
        {
            return false;
        }

        return LooksLikeTaskFactoryStartNewCleanup(text) ||
               IsLowSignalHeaderOpportunity(text) ||
               IsMarkerInterfaceOnlyOpportunity(text) ||
               IsConfigurableConstantOnlyOpportunity(text) ||
               IsDuplicateNullTokenOpportunity(text) ||
               IsAlreadyCachedTokenOpportunity(text) ||
               IsHotReloadOnlyOpportunity(text) ||
               IsConfigureAwaitOnlyOpportunity(text) ||
               IsEndpointConfigurationOnlyOpportunity(text) ||
               IsNamedHttpClientOnlyOpportunity(text) ||
               IsSemaphoreSlimDisposeOnlyOpportunity(text) ||
               IsHelperExtractionOnlyOpportunity(text) ||
               IsNullableAnnotationOnlyOpportunity(text) ||
               IsDateTimeReadabilityOnlyOpportunity(text) ||
               IsFutureSerializerCompatibilityOpportunity(text) ||
               IsFutureEqualityOnlyOpportunity(text) ||
               ContainsAny(
            text,
            "<returns>",
            "xml-тег",
            "xml tag",
            "неиспользуемый импорт",
            "unused using",
            "опечатк",
            "комментар",
            "переименовать параметр",
            "неоднозначное имя параметра");
    }

    private static bool LooksLikeTaskFactoryStartNewAsyncIoConcern(string text)
    {
        return ContainsAny(text, "task.factory.startnew", "startnew") &&
               ContainsAny(
                   text,
                   "async i/o",
                   "token refresh",
                   "refresh токен",
                   "refresh токена",
                   "http",
                   "cancellation",
                   "fire-and-forget",
                   "unobserved",
                   "exception",
                   "исключ",
                   "cpu-bound");
    }

    private static bool IsHighSignalDuplicationOpportunity(string text)
    {
        return ContainsAny(text, "дублирован", "дублируется", "duplicate", "повторяющ", "идентичн") &&
               ContainsAny(
                   text,
                   "enrich",
                   "обогащ",
                   "mapping",
                   "мапп",
                   "business-rule",
                   "бизнес",
                   "readmodel",
                   "sql",
                   "cache",
                   "кэш",
                   "контракт");
    }

    private static bool LooksLikeTaskFactoryStartNewCleanup(string text)
    {
        return ContainsAny(text, "task.factory.startnew") &&
               !LooksLikeTaskFactoryStartNewAsyncIoConcern(text) &&
               ContainsAny(
                   text,
                   "task.run",
                   "base(taskfactory)",
                   "упрощ",
                   "избыточ",
                   "лишн",
                   "современн",
                   "читаем",
                   "идиомат",
                   "поддерживаем",
                   "достаточно передать");
    }

    private static bool IsLowSignalHeaderOpportunity(string text)
    {
        return ContainsAny(text, "tryaddwithoutvalidation", "безопасное добавление", "конфликте заголовков") &&
               ContainsAny(text, "correlationid", "headers.add", "request.headers.add");
    }

    private static bool IsMarkerInterfaceOnlyOpportunity(string text)
    {
        return ContainsAny(text, "пустой интерфейс", "marker-interface", "маркерн") &&
               ContainsAny(text, "не добавляет", "без специфичных", "объединение", "наследует");
    }

    private static bool IsConfigurableConstantOnlyOpportunity(string text)
    {
        return ContainsAny(text, "конфигурируем", "конфигурац", "опции", "options") &&
               ContainsAny(text, "интервал", "порог", "threshold", "timespan", "hardcode", "хардкод", "1 и 15 минут", "60 секунд") &&
               !ContainsAny(text, "отсутств", "required", "validateonstart", "валидац", "null", "секрет", "secret");
    }

    private static bool IsDuplicateNullTokenOpportunity(string text)
    {
        return ContainsAny(text, "null токен", "null-токен", "токен на null", "токена на null", "accesstoken на null", "accessToken на null", "отсутствия сервисного токена", "отсутствие сервисного токена") &&
               ContainsAny(text, "authorization", "authenticationheadervalue", "заголов", "без токена", "без заголовка");
    }

    private static bool IsAlreadyCachedTokenOpportunity(string text)
    {
        return ContainsAny(text, "кэширование токена", "кеширование токена", "кэшировать токен", "кешировать токен") &&
               !ContainsAny(text, "ключ", "tenant", "scope", "user", "инвалидац", "ttl");
    }

    private static bool IsHotReloadOnlyOpportunity(string text)
    {
        return ContainsAny(text, "ioptionssnapshot", "ioptionsmonitor", "горяч", "hot reload", "перезагрузк") &&
               ContainsAny(text, "конфигурац", "options", "settings");
    }

    private static bool IsConfigureAwaitOnlyOpportunity(string text)
    {
        return ContainsAny(text, "configureawait(false)", "захвату контекста синхронизации", "synchronizationcontext");
    }

    private static bool IsEndpointConfigurationOnlyOpportunity(string text)
    {
        return ContainsAny(text, "жёстко заданный путь", "жестко заданный путь", "hardcoded endpoint", "токен-эндпоинт", "connect/token") &&
               ContainsAny(text, "опции", "options", "конфигурац", "переопредел");
    }

    private static bool IsNamedHttpClientOnlyOpportunity(string text)
    {
        return ContainsAny(text, "именованный httpclient", "типизированный клиент", "named httpclient", "baseaddress") &&
               ContainsAny(text, "централизован", "предварительной настройки", "переопределяет baseaddress", "polly");
    }

    private static bool IsSemaphoreSlimDisposeOnlyOpportunity(string text)
    {
        return ContainsAny(text, "semaphoreslim") &&
               ContainsAny(text, "dispose", "idisposable", "освобожд", "утилизац") &&
               !ContainsAny(text, "availablewaithandle", "loop", "цикл", "каждую итерац", "многократн");
    }

    private static bool IsHelperExtractionOnlyOpportunity(string text)
    {
        return ContainsAny(text, "вынести", "helper", "вспомогательн", "дублируется", "дублирование", "расхождениям") &&
               ContainsAny(text, "метод", "asynclazy", "создание");
    }

    private static bool IsNullableAnnotationOnlyOpportunity(string text)
    {
        return ContainsAny(text, "nullable-аннотац", "nullable annotations", "<nullable>enable", "статический анализ") &&
               !ContainsAny(text, "return null", "возвращает null", "authorization", "bearer", "http-заголов");
    }

    private static bool IsDateTimeReadabilityOnlyOpportunity(string text)
    {
        return ContainsAny(text, "упростить сравнение времени", "читаемость", "аналогичный стиль") &&
               ContainsAny(text, "datetime", "timespan", "addminutes");
    }

    private static bool IsFutureSerializerCompatibilityOpportunity(string text)
    {
        return ContainsAny(text, "system.text.json", "jsonpropertyname") &&
               ContainsAny(text, "в будущем", "совместимост", "если потребуется", "newtonsoft");
    }

    private static bool IsFutureEqualityOnlyOpportunity(string text)
    {
        return ContainsAny(text, "equals/gethashcode", "equals", "gethashcode", "record") &&
               ContainsAny(text, "в будущем", "future", "hashset", "ключа словаря", "reference equality") &&
               ContainsAny(text, "корректность", "не страдает", "может дать неожиданные", "future");
    }

    private static FindingFailureKind ClassifyFinding(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        if (ContainsAll(text, "nonnull", "null") ||
            ContainsAll(text, "non-null", "null") ||
            ContainsAll(text, "ненул", "null") ||
            ContainsAll(text, "nullability", "null") ||
            ContainsAll(text, "nullable", "null") ||
            ContainsAll(text, "nullable", "return null"))
        {
            return FindingFailureKind.NonNullableContractReturnsNull;
        }

        if (ContainsAny(text, "validateonstart", "startup-валидац", "валидации при старт", "без startup") ||
            ContainsAll(text, "options", "validation"))
        {
            return FindingFailureKind.OptionsValidation;
        }

        if (ContainsAny(text, "getsection", "sectionname", "section name", "секции конфиг", "секция конфига", "options привязаны") ||
            ContainsAll(text, "options", "section"))
        {
            return FindingFailureKind.OptionsBindingSection;
        }

        if (ContainsAll(text, "join", "duplicate") ||
            ContainsAll(text, "join", "дубли") ||
            ContainsAll(text, "join", "размнож"))
        {
            return FindingFailureKind.SqlRowMultiplication;
        }

        if (ContainsAll(text, "groupby", "first") ||
            ContainsAny(text, "недетерминированный выбор", "недетерминированно"))
        {
            return FindingFailureKind.GroupByFirstWithoutOrder;
        }

        if (ContainsAny(text, "isbasic", "isbcallowed", "бизнес-фильтр", "разрешённые регионы", "разрешенные регионы"))
        {
            return FindingFailureKind.SqlBusinessFilter;
        }

        if (ContainsAny(text, "seedordertable", "seed-данн", "seed-данные", "seed данные"))
        {
            return FindingFailureKind.TestMissingSeedMember;
        }

        if (ContainsAny(text, "pagesize", "page size"))
        {
            return FindingFailureKind.UnboundedPageSize;
        }

        if ((ContainsAny(text, "jwt", "validto", "validfrom", "datetime.now", "datetimeoffset.now", "exp") ||
             ContainsAny(text, "токен", "jwt-токена")) &&
            ContainsAny(text, "utc", "локальн", "local time", "datetime.now", "datetimeoffset.now"))
        {
            return FindingFailureKind.JwtUtcComparedWithLocalTime;
        }

        if (ContainsAny(text, "task.factory.startnew", "startnew") &&
            ContainsAny(
                text,
                "async i/o",
                "token refresh",
                "refresh токен",
                "refresh токена",
                "получения токен",
                "асинхронн",
                "http",
                "неблокир",
                "thread pool",
                "thread-pool",
                "пул",
                "cpu-bound"))
        {
            return FindingFailureKind.TaskFactoryStartNewAsyncIo;
        }

        if (ContainsAny(text, "secret", "password", "token", "signing key", "секрет"))
        {
            return FindingFailureKind.SecretLikeConfig;
        }

        if (ContainsAll(text, "executeupdate", "transaction") ||
            ContainsAll(text, "bulk", "транзакц"))
        {
            return FindingFailureKind.NonAtomicBulkUpdate;
        }

        if (ContainsAll(text, "kafka", "publish") ||
            ContainsAny(text, "pubsub", "видимости"))
        {
            return FindingFailureKind.KafkaCachePublishFiltering;
        }

        if (ContainsAny(text, "dotnet restore", "dotnet build", "nu110"))
        {
            return FindingFailureKind.RestoreBuildFailure;
        }

        return FindingFailureKind.Unknown;
    }

    private static bool HaveSharedSemanticAnchor(ReviewFinding left, ReviewFinding right)
    {
        if (PathsMatch(left.File, right.File) && LineRangesAreClose(left, right))
        {
            return true;
        }

        var leftAnchors = ExtractSemanticAnchors(left);
        var rightAnchors = ExtractSemanticAnchors(right);
        return leftAnchors.Count > 0 &&
               rightAnchors.Count > 0 &&
               leftAnchors.Overlaps(rightAnchors);
    }

    private static HashSet<string> ExtractSemanticAnchors(ReviewFinding finding)
    {
        var text = BuildFindingText(finding);
        return Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]{3,}")
            .Select(match => match.Value)
            .Where(IsMeaningfulAnchor)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsMeaningfulAnchor(string value)
    {
        if (value.Length < 4)
        {
            return false;
        }

        if (GenericAnchorWords.Contains(value))
        {
            return false;
        }

        return value.Any(char.IsUpper) ||
               value.Any(char.IsDigit) ||
               value.Contains('_', StringComparison.Ordinal) ||
               value.StartsWith("is", StringComparison.OrdinalIgnoreCase);
    }

    private static double ComputeTextSimilarity(ReviewFinding left, ReviewFinding right)
    {
        var leftTerms = ExtractSignificantTerms(BuildFindingText(left)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTerms = ExtractSignificantTerms(BuildFindingText(right)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTerms.Count == 0 || rightTerms.Count == 0)
        {
            return 0d;
        }

        var intersection = leftTerms.Intersect(rightTerms, StringComparer.OrdinalIgnoreCase).Count();
        return intersection == 0
            ? 0d
            : (2d * intersection) / (leftTerms.Count + rightTerms.Count);
    }

    private static bool HaveMeaningfulExactCodeMatch(string left, string right)
    {
        var normalizedLeft = NormalizeMatchValue(left);
        var normalizedRight = NormalizeMatchValue(right);
        if (normalizedLeft.Length < 12 || normalizedRight.Length < 12)
        {
            return false;
        }

        return normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal) ||
               normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
               normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal);
    }

    private static string NormalizeMatchValue(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray())
            .Trim()
            .ToLowerInvariant();
    }

    private static string BuildFindingText(ReviewFinding finding)
    {
        return $"{finding.File} {finding.LineHint} {finding.Title} {finding.Description} {finding.ExistingCode} {finding.Suggestion}";
    }

    private IReadOnlyList<ReviewFinding> SuppressContradictedScoutFindings(
        IReadOnlyList<ReviewFinding> candidates,
        string diffText,
        Guid runId)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var supported = new List<ReviewFinding>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (IsContradictedMissingRegionEnrichmentFinding(candidate, diffText))
            {
                logger.LogInformation(
                    "Suppressed scout finding contradicted by diff evidence for run {RunId}: file={File}, title={Title}",
                    runId,
                    candidate.File,
                    candidate.Title);
                continue;
            }

            supported.Add(candidate);
        }

        return supported;
    }

    private static bool IsContradictedMissingRegionEnrichmentFinding(ReviewFinding finding, string diffText)
    {
        var text = BuildFindingText(finding);
        if (!ContainsAny(text, "enrichwithregiondata"))
        {
            return false;
        }

        if (!ContainsAny(
                text,
                "not added",
                "does not call",
                "not call",
                "не вызван",
                "не вызывает",
                "не был добавлен вызов",
                "не добавлен вызов",
                "отсутствует вызов"))
        {
            return false;
        }

        if (!ContainsAny(text, "excel") ||
            !ContainsAny(text, "OrderForExcelFileDbModel", "GetOrderListForExcel"))
        {
            return false;
        }

        return ContainsAny(diffText, "GetOrderListForExcel", "OrderForExcelFileDbModel") &&
               ContainsAny(
                   diffText,
                   "+        orders.EnrichWithRegionData(_regionCacheRepository);",
                   "+        orders.EnrichWithRegionData(regionCacheRepository);");
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAll(string text, params string[] needles)
    {
        return needles.All(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> GenericAnchorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "code",
        "config",
        "configuration",
        "contract",
        "description",
        "existing",
        "finding",
        "getsection",
        "implementation",
        "interface",
        "options",
        "public",
        "return",
        "section",
        "service",
        "string",
        "suggestion",
        "validate",
        "validateonstart"
    };

    private enum FindingFailureKind
    {
        Unknown,
        NonNullableContractReturnsNull,
        OptionsBindingSection,
        OptionsValidation,
        SqlBusinessFilter,
        SqlRowMultiplication,
        GroupByFirstWithoutOrder,
        TestMissingSeedMember,
        UnboundedPageSize,
        JwtUtcComparedWithLocalTime,
        TaskFactoryStartNewAsyncIo,
        SecretLikeConfig,
        NonAtomicBulkUpdate,
        KafkaCachePublishFiltering,
        RestoreBuildFailure
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
            ReviewRun run,
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

        var response = await CompleteWithProgressHeartbeatAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = 0,
                ExpectJson = true,
                SystemPrompt = reviewPromptFactory.BuildFinalNormalizationSystemPrompt(),
                UserPrompt = reviewPromptFactory.BuildFinalNormalizationUserPrompt(findings, opportunities)
            },
            run,
            ReviewPipelineStage.FindingsNormalization,
            76,
            "Финальная нормализация findings: модель отвечает",
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
        var additionalRules = BuildPrimaryChunkReviewAdditionalRules();

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
                                additionalRules),
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
        var maxToolRequests = Math.Clamp(reviewPipelineOptions.Value.MaxChunkToolRequests, 0, 3);
        if (maxToolRequests == 0 && envelope.ToolRequests.Count > 0)
        {
            logger.LogInformation(
                "Primary chunk tool loop is disabled for run {RunId}, chunk {ChunkIndex}: requested={RequestedToolRequests}",
                run.Id,
                chunkIndex + 1,
                envelope.ToolRequests.Count);
            envelope = envelope with { NeedMoreContext = false, ToolRequests = [] };
        }
        else if (envelope.ToolRequests.Count > maxToolRequests)
        {
            logger.LogInformation(
                "Primary chunk tool requests capped for run {RunId}, chunk {ChunkIndex}: requested={RequestedToolRequests}, allowed={AllowedToolRequests}",
                run.Id,
                chunkIndex + 1,
                envelope.ToolRequests.Count,
                maxToolRequests);
            envelope = envelope with
            {
                ToolRequests = envelope.ToolRequests.Take(maxToolRequests).ToArray()
            };
        }

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

    private string BuildPrimaryChunkReviewAdditionalRules()
    {
        var maxToolRequests = Math.Clamp(reviewPipelineOptions.Value.MaxChunkToolRequests, 0, 3);
        if (maxToolRequests == 0)
        {
            return """
                Tool request rules for this run:
                - Chunk tool-loop is disabled for latency. Return need_more_context=false and tool_requests=[].
                - If missing cross-file context prevents a confident finding, omit that candidate or keep it as a cautious opportunity only when grounded in the changed lines.
                """;
        }

        return ReviewPromptSpecialRules.PrimaryReviewToolRequestRules + "\n" + $"""
            Run-specific tool budget:
            - Request at most {maxToolRequests} workspace tool(s) for this chunk.
            - If several lookups look useful, choose only the single highest-confidence blocker first.
            - Do not request tools for optional hardening ideas or low-signal opportunities.
            """;
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

    private void LogFullContextToolRequestDecision(
        ReviewRun run,
        int iteration,
        ChunkReviewAgentEnvelope envelope)
    {
        logger.LogInformation(
            "Full-context tool decision for run {RunId}, iteration {Iteration}: NeedMoreContext={NeedMoreContext}, ToolRequestsCount={ToolRequestsCount}",
            run.Id,
            iteration,
            envelope.NeedMoreContext,
            envelope.ToolRequests.Count);

        for (var index = 0; index < envelope.ToolRequests.Count; index++)
        {
            var request = envelope.ToolRequests[index];
            logger.LogInformation(
                "Full-context tool request {RequestIndex} for run {RunId}, iteration {Iteration}: Tool={ToolName}, Query={Query}, FilePath={FilePath}, PathScope={PathScope}, StartLine={StartLine}, MaxLines={MaxLines}, Reason={Reason}",
                index + 1,
                run.Id,
                iteration,
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

    private void LogFullContextToolResponses(
        ReviewRun run,
        int iteration,
        IReadOnlyList<ReviewWorkspaceToolResponse> toolResponses,
        string responseSource)
    {
        logger.LogInformation(
            "Full-context tool responses for run {RunId}, iteration {Iteration}, source {ResponseSource}: ToolResponsesCount={ToolResponsesCount}",
            run.Id,
            iteration,
            responseSource,
            toolResponses.Count);

        for (var index = 0; index < toolResponses.Count; index++)
        {
            var response = toolResponses[index];
            logger.LogInformation(
                "Full-context tool response {ResponseIndex} for run {RunId}, iteration {Iteration}, source {ResponseSource}: Tool={ToolName}, FilePath={FilePath}, Source={Source}, Lines={StartLine}-{EndLine}, Preview={Preview}",
                index + 1,
                run.Id,
                iteration,
                responseSource,
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

    private async Task ReusePreviousReviewResultAsync(
        ReviewRun run,
        ReviewRun previousRun,
        CancellationToken cancellationToken)
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
            FindingsComparison = reusedComparison,
            SemanticCodeContext = previousRun.Artifacts.SemanticCodeContext,
            ExternalReview = previousRun.Artifacts.ExternalReview
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
    }

    private async Task<IncrementalReviewScope> BuildIncrementalReviewScopeAsync(
        ReviewRun? previousRun,
        PreprocessedDiff fullPreprocessed,
        ReviewExecutionRequest request,
        ReviewRun run,
        CodeGraph? roslynGraph,
        string? roslynWorkspaceRoot,
        ReviewPipelineOptions pipelineOptions,
        CancellationToken cancellationToken)
    {
        if (!pipelineOptions.UseIncrementalReviewMode ||
            request.ForceRerun ||
            previousRun is null ||
            previousRun.Status != ReviewRunStatus.Completed ||
            string.IsNullOrWhiteSpace(previousRun.Artifacts.DiffText))
        {
            return IncrementalReviewScope.Full(fullPreprocessed);
        }

        var deltaDiff = BuildIncrementalDiffText(
            previousRun.Artifacts.DiffText,
            fullPreprocessed.FilteredDiffText,
            out var stats);
        if (string.IsNullOrWhiteSpace(deltaDiff))
        {
            return IncrementalReviewScope.Full(fullPreprocessed);
        }

        var deltaPreprocessed = diffPreprocessor.Process(deltaDiff);
        if (deltaPreprocessed.ChangedFiles.Count == 0)
        {
            return IncrementalReviewScope.Full(fullPreprocessed);
        }

        if (roslynGraph is not null && !string.IsNullOrWhiteSpace(roslynWorkspaceRoot))
        {
            deltaPreprocessed = await graphAwareChunker.AugmentWithGraphChunksAsync(
                deltaPreprocessed,
                roslynGraph,
                roslynWorkspaceRoot,
                pipelineOptions,
                cancellationToken);
        }

        logger.LogInformation(
            "Incremental review mode enabled for run {RunId}: baseline={BaselineRunId}, currentFiles={CurrentFiles}, baselineFiles={BaselineFiles}, deltaFiles={DeltaFiles}, deltaChars={DeltaChars}",
            run.Id,
            previousRun.Id,
            stats.CurrentSections,
            stats.PreviousSections,
            stats.DeltaSections,
            deltaDiff.Length);

        return new IncrementalReviewScope(
            deltaPreprocessed,
            true,
            stats.CurrentSections,
            stats.PreviousSections,
            stats.DeltaSections);
    }

    private static string BuildIncrementalDiffText(
        string previousDiffText,
        string currentDiffText,
        out IncrementalDiffStats stats)
    {
        var previousSections = ParseDiffSections(previousDiffText);
        var currentSections = ParseDiffSections(currentDiffText);
        stats = new IncrementalDiffStats(
            currentSections.Count,
            previousSections.Count,
            currentSections.Count);

        if (previousSections.Count == 0 || currentSections.Count == 0)
        {
            return currentDiffText;
        }

        var previousByFile = previousSections
            .GroupBy(section => NormalizePath(section.FilePath))
            .ToDictionary(
                group => group.Key,
                group => group.Select(section => NormalizeDiffText(section.Patch)).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var deltaSections = currentSections
            .Where(section =>
            {
                var file = NormalizePath(section.FilePath);
                return !previousByFile.TryGetValue(file, out var previousPatches) ||
                       !previousPatches.Contains(NormalizeDiffText(section.Patch));
            })
            .ToArray();

        stats = stats with { DeltaSections = deltaSections.Length };
        return string.Concat(deltaSections.Select(section => EnsureTrailingNewLine(section.Patch)));
    }

    private static string NormalizeDiffText(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string EnsureTrailingNewLine(string value)
        => value.EndsWith('\n') ? value : value + "\n";

    private static IReadOnlyList<ReviewFinding> FilterIncrementalCandidateFindings(
        IReadOnlyList<ReviewFinding> candidates,
        ReviewRun previousRun,
        PreprocessedDiff reviewPreprocessed)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var deltaFiles = reviewPreprocessed.ChangedFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(candidate => deltaFiles.Count == 0 || deltaFiles.Contains(NormalizePath(candidate.File)))
            .Where(candidate => previousRun.Findings.All(previous => !CoversFinding(previous, candidate)))
            .ToArray();
    }

    private static FindingsComparisonSnapshot BuildIncrementalFindingsComparison(
        ReviewRun previousRun,
        IReadOnlyList<ReviewFinding> newFindings)
    {
        return new FindingsComparisonSnapshot
        {
            PreviousRunId = previousRun.Id,
            PreviousFindingsCount = previousRun.Findings.Count,
            CurrentFindingsCount = newFindings.Count,
            NewFindingsCount = newFindings.Count,
            StillRelevantFindingsCount = previousRun.Findings.Count,
            ResolvedFindingsCount = 0,
            IsDiffUnchanged = false,
            NewFindings = newFindings,
            StillRelevantFindings = previousRun.Findings,
            ResolvedFindings = []
        };
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

    private sealed record IncrementalReviewScope(
        PreprocessedDiff ReviewPreprocessed,
        bool IsIncremental,
        int CurrentSections,
        int PreviousSections,
        int DeltaSections)
    {
        public static IncrementalReviewScope Full(PreprocessedDiff preprocessed)
            => new(preprocessed, false, preprocessed.ChangedFiles.Count, 0, preprocessed.ChangedFiles.Count);
    }

    private sealed record IncrementalDiffStats(
        int CurrentSections,
        int PreviousSections,
        int DeltaSections);

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
        var tokenMarker = " " + marker;
        var start = header.IndexOf(tokenMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += tokenMarker.Length;
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

    private sealed record DiffFindingEvidence(
        string FilePath,
        string PatchText,
        IReadOnlyList<DiffLineRange> HunkRanges)
    {
        public string NormalizedPatchText { get; } = NormalizeEvidenceText(PatchText);

        public IReadOnlySet<string> SemanticAnchors { get; } = Regex.Matches(PatchText, @"[A-Za-z_][A-Za-z0-9_]{3,}")
            .Select(match => match.Value)
            .Where(IsMeaningfulAnchor)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record DiffLineRange(int Start, int End);

    private sealed record DiffHunk(int NewLineStart, int NewLineEnd, string Content, int OriginalStartIndex);
}
