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
    IRepositoryFileContentService repositoryFileContentService,
    ILlmStageRouter llmStageRouter,
    ILlmCompletionService llmCompletionService,
    IReviewPromptFactory reviewPromptFactory,
    IFindingsNormalizer findingsNormalizer,
    IFindingsComparisonService findingsComparisonService,
    IMarkdownReportBuilder markdownReportBuilder,
    IReviewPublisher reviewPublisher,
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
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                InlineComments = inlineComments,
                ReviewedFiles = reviewedFiles,
                FindingsComparison = findingsComparison
            });
            await reviewRunRepository.UpdateAsync(run, cancellationToken);
            await PersistAndPublishAsync(run, "File review workspace generated", ReviewPipelineStage.FinalSynthesis, 90, cancellationToken);

            var fullReport = markdownReportBuilder.BuildFullReport(run.Target.Title, description, findings, findingsComparison);
            var summaryComment = markdownReportBuilder.BuildSummaryComment(run.Target.Title, description, findings, findingsComparison);
            run.UpdateArtifacts(new ReviewArtifacts
            {
                DiffText = preprocessed.FilteredDiffText,
                ChangedFiles = preprocessed.ChangedFiles,
                ChangeDescription = changeSummary.Description,
                ChangeDiagramMermaid = changeSummary.DiagramMermaid,
                MarkdownReport = fullReport,
                SummaryComment = summaryComment,
                InlineComments = inlineComments,
                ReviewedFiles = reviewedFiles,
                FindingsComparison = findingsComparison
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
                InlineComments = inlineComments,
                ReviewedFiles = reviewedFiles,
                FindingsComparison = findingsComparison
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
        var context = ContextExtractor.Build(thread.LineNumber, fullContent, diffPatch, thread.ExistingCode);
        var relevantDiffHunk = ContextExtractor.BuildRelevantDiffHunk(
            thread.LineNumber,
            diffPatch,
            thread.ExistingCode,
            context.StartLine,
            context.EndLine);
        return thread with
        {
            ContextBlock = context.Block,
            ContextStartLine = context.StartLine,
            ContextEndLine = context.EndLine,
            RelevantDiffHunk = relevantDiffHunk
        };
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

    private static class ContextExtractor
    {
        public static (string Block, int StartLine, int EndLine) Build(
            int lineNumber,
            string fullContent,
            string diffPatch,
            string snippet)
        {
            var fromFile = BuildFromFullFile(lineNumber, fullContent);
            if (!string.IsNullOrWhiteSpace(fromFile.Block))
            {
                return fromFile;
            }

            return BuildFromPatch(diffPatch, snippet);
        }

        private static (string Block, int StartLine, int EndLine) BuildFromFullFile(int lineNumber, string fullContent)
        {
            if (string.IsNullOrWhiteSpace(fullContent) || lineNumber <= 0)
            {
                return (string.Empty, 0, 0);
            }

            var lines = fullContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lineNumber > lines.Length)
            {
                return (string.Empty, 0, 0);
            }

            var startLine = Math.Max(1, lineNumber - 6);
            var endLine = Math.Min(lines.Length, lineNumber + 10);

            var declarationStart = FindDeclarationStart(lines, lineNumber);
            if (declarationStart > 0)
            {
                startLine = Math.Min(startLine, declarationStart);
            }

            var contextLines = new List<string>(endLine - startLine + 1);
            for (var currentLine = startLine; currentLine <= endLine; currentLine++)
            {
                contextLines.Add($"{currentLine,4}: {lines[currentLine - 1]}");
            }

            return (string.Join('\n', contextLines), startLine, endLine);
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
