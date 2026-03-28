using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

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
    IFindingsNormalizer findingsNormalizer,
    IFindingsComparisonService findingsComparisonService,
    IMarkdownReportBuilder markdownReportBuilder,
    IReviewPublisher reviewPublisher,
    IOptions<ReviewPipelineOptions> reviewPipelineOptions,
    ILogger<ReviewRunExecutor> logger)
    : IReviewRunExecutor
{
    public async Task ExecuteAsync(Guid runId, ReviewExecutionRequest request, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");
        var previousRun = await reviewRunRepository.FindLatestCompletedForTargetAsync(run.Target, run.CreatedAt, cancellationToken);
        DiffAcquisitionResult? diffResult = null;

        try
        {
            run.Start();
            await PersistAndPublishAsync(run, "Review started", ReviewPipelineStage.DiffAcquisition, 2, cancellationToken);

            diffResult = await AcquireDiffAsync(request, cancellationToken);
            run.UpdateMetadata(diffResult.ServiceName, diffResult.AuthorName, diffResult.PullRequestTitle);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "Diff acquired", ReviewPipelineStage.Preprocessing, 15, cancellationToken);

            var preprocessed = diffPreprocessor.Process(diffResult.DiffText);
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

            if (previousRun is not null && HasNoChangesSincePreviousReview(previousRun, preprocessed))
            {
                var reusedComparison = findingsComparisonService.Compare(
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
                await reviewRunRepository.UpdateAsync(run, cancellationToken);
                await TryIndexSemanticArtifactsAsync(run, cancellationToken);
                await reviewProgressStore.PublishAsync(
                    new ReviewProgressUpdate(run.Id, run.Status, run.CurrentStage, run.ProgressPercent, run.CurrentMessage, DateTimeOffset.UtcNow, true),
                    cancellationToken);
                reviewProgressStore.Complete(run.Id);
                return;
            }

            var changeSummary = await GenerateChangeSummaryAsync(run.DisplayTitle, preprocessed, request, cancellationToken);
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

            var rawFindings = await ReviewChunksAsync(description, preprocessed.ReviewChunks, request, run, cancellationToken);
            await PersistAndPublishAsync(run, "Raw findings collected", ReviewPipelineStage.FindingsNormalization, 75, cancellationToken);

            var findings = findingsNormalizer.Normalize(rawFindings);
            var findingsComparison = previousRun is null
                ? null
                : findingsComparisonService.Compare(
                    previousRun.Id,
                    previousRun.Findings,
                    findings);
            run.UpdateFindings(findings);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, $"Normalized {findings.Count} findings", ReviewPipelineStage.FinalSynthesis, 87, cancellationToken);

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
                FindingsComparison = findingsComparison
            };

            run.Complete(artifacts, findings, publishSucceeded);
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await TryIndexSemanticArtifactsAsync(run, cancellationToken);
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

    private async Task<ChangeSummaryResult> GenerateChangeSummaryAsync(
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
        var diffSnippet = preprocessed.ReviewContextDiffText.Length > maxChangeSummaryCharacters
            ? preprocessed.ReviewContextDiffText[..maxChangeSummaryCharacters]
            : preprocessed.ReviewContextDiffText;

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
                    DiagramMermaid = NormalizeMermaidCode(diagram),
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
