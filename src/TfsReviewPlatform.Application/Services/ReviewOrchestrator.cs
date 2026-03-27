using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    IBranchRepositoryLookupService branchRepositoryLookupService,
    ILlmStageRouter llmStageRouter,
    ILlmCompletionService llmCompletionService,
    IFindingsNormalizer findingsNormalizer,
    IFindingsComparisonService findingsComparisonService,
    IReviewPublisher reviewPublisher,
    IMarkdownReportBuilder markdownReportBuilder,
    ILogger<ReviewOrchestrator> logger)
    : IReviewOrchestrator
{
    private static readonly JsonSerializerOptions InlineDiscussionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const string RepositoriesRootEnvironmentVariable = "REPOSITORIES_ROOT";

    public async Task<ReviewRunDto> StartPullRequestReviewAsync(
        StartPullRequestReviewRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedPullRequestUrl = PullRequestPlatformDetector.Normalize(request.PullRequestUrl);
        Validate(reviewRequestValidator.Validate(request));

        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.PullRequest,
            normalizedPullRequestUrl,
            normalizedPullRequestUrl,
            null,
            null,
            null,
            null);

        var executionRequest = new ReviewExecutionRequest
        {
            TargetKind = ReviewTargetKind.PullRequest,
            Target = target,
            ProviderProfileId = request.ProviderProfileId,
            PublishMode = request.PublishMode,
            PullRequestAccessToken = ResolvePullRequestAccessToken(
                normalizedPullRequestUrl,
                request.AccessToken ?? request.AzureDevOpsAccessToken),
            StageOverrides = request.StageOverrides
        };

        return await EnqueueAsync(target, executionRequest, cancellationToken);
    }

    public async Task<ReviewRunDto> StartBranchReviewAsync(
        StartBranchReviewRequest request,
        CancellationToken cancellationToken)
    {
        Validate(reviewRequestValidator.Validate(request));

        var repositoryPath = ResolveRepositoryPath(request.RepositoryName);
        var title = $"{request.RepositoryName}: {request.SourceBranch} -> {request.TargetBranch}";
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.BranchComparison,
            title,
            null,
            repositoryPath,
            request.RepositoryName,
            request.SourceBranch,
            request.TargetBranch);

        var executionRequest = new ReviewExecutionRequest
        {
            TargetKind = ReviewTargetKind.BranchComparison,
            Target = target,
            ProviderProfileId = request.ProviderProfileId,
            PublishMode = request.PublishMode,
            StageOverrides = request.StageOverrides
        };

        return await EnqueueAsync(target, executionRequest, cancellationToken);
    }

    private static string ResolveRepositoryPath(string repositoryName)
    {
        var resolvedRoot = ResolveRepositoriesRootOrNull();
        if (resolvedRoot is null)
        {
            throw new InvalidOperationException(
                $"Для сравнения веток требуется переменная окружения {RepositoriesRootEnvironmentVariable} с корневой папкой репозиториев.");
        }

        var repositoryPath = Path.GetFullPath(Path.Combine(resolvedRoot, repositoryName));
        var relativePath = Path.GetRelativePath(resolvedRoot, repositoryPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Название репозитория должно указывать на папку внутри настроенного корня репозиториев.");
        }

        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidOperationException(
                $"Репозиторий '{repositoryName}' не найден внутри '{resolvedRoot}'. Проверь настройку {RepositoriesRootEnvironmentVariable} и имя папки.");
        }

        return repositoryPath;
    }

    private static string? ResolveRepositoriesRootOrNull()
    {
        var repositoriesRoot = Environment.GetEnvironmentVariable(RepositoriesRootEnvironmentVariable)?.Trim();
        return string.IsNullOrWhiteSpace(repositoriesRoot)
            ? null
            : Path.GetFullPath(repositoriesRoot);
    }

    public async Task<ReviewRunDto?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken);
        return run?.ToDto();
    }

    public async Task<ReviewHistoryDto> GetPullRequestHistoryAsync(string pullRequestUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            throw new InvalidOperationException("Pull request URL cannot be empty.");
        }

        var normalizedUrl = PullRequestPlatformDetector.Normalize(pullRequestUrl);
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.PullRequest,
            normalizedUrl,
            normalizedUrl,
            null,
            null,
            null,
            null);

        var runs = await reviewRunRepository.ListForTargetAsync(target, 20, cancellationToken);
        var baselineRunId = runs
            .Where(run => run.Status == ReviewRunStatus.Completed)
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => (Guid?)run.Id)
            .FirstOrDefault();

        return runs.ToHistoryDto(baselineRunId);
    }

    public async Task DeletePullRequestHistoryAsync(string pullRequestUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            throw new InvalidOperationException("Pull request URL cannot be empty.");
        }

        var normalizedUrl = PullRequestPlatformDetector.Normalize(pullRequestUrl);
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.PullRequest,
            normalizedUrl,
            normalizedUrl,
            null,
            null,
            null,
            null);

        await reviewRunRepository.DeleteForTargetAsync(target, cancellationToken);
    }

    public async Task<ReviewHistoryDto> GetBranchReviewHistoryAsync(
        string repositoryName,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            throw new InvalidOperationException("Название репозитория не может быть пустым.");
        }

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            throw new InvalidOperationException("Исходная ветка не может быть пустой.");
        }

        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            throw new InvalidOperationException("Целевая ветка не может быть пустой.");
        }

        var repositoryPath = ResolveRepositoryPath(repositoryName);
        var title = $"{repositoryName}: {sourceBranch} -> {targetBranch}";
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.BranchComparison,
            title,
            null,
            repositoryPath,
            repositoryName,
            sourceBranch,
            targetBranch);

        var runs = await reviewRunRepository.ListForTargetAsync(target, 20, cancellationToken);
        var baselineRunId = runs
            .Where(run => run.Status == ReviewRunStatus.Completed)
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => (Guid?)run.Id)
            .FirstOrDefault();

        return runs.ToHistoryDto(baselineRunId);
    }

    public async Task DeleteBranchReviewHistoryAsync(
        string repositoryName,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            throw new InvalidOperationException("Название репозитория не может быть пустым.");
        }

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            throw new InvalidOperationException("Исходная ветка не может быть пустой.");
        }

        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            throw new InvalidOperationException("Целевая ветка не может быть пустой.");
        }

        var repositoryPath = ResolveRepositoryPath(repositoryName);
        var title = $"{repositoryName}: {sourceBranch} -> {targetBranch}";
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.BranchComparison,
            title,
            null,
            repositoryPath,
            repositoryName,
            sourceBranch,
            targetBranch);

        await reviewRunRepository.DeleteForTargetAsync(target, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetBranchRepositorySuggestionsAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var repositoriesRoot = ResolveRepositoriesRootOrNull();
        if (repositoriesRoot is null)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        return branchRepositoryLookupService.GetRepositorySuggestionsAsync(repositoriesRoot, query, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetBranchSourceSuggestionsAsync(
        string repositoryName,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var repositoryPath = ResolveRepositoryPath(repositoryName);
        return branchRepositoryLookupService.GetBranchSuggestionsAsync(repositoryPath, query, cancellationToken);
    }

    public async Task<ReviewRunDto> PublishInlineCommentAsync(Guid runId, Guid commentId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        if (run.Target.Kind != ReviewTargetKind.PullRequest || string.IsNullOrWhiteSpace(run.Target.PullRequestUrl))
        {
            throw new InvalidOperationException("Только pull request ревью можно публиковать обратно в исходную платформу.");
        }

        var accessToken = ResolvePullRequestAccessToken(run.Target.PullRequestUrl, null);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(BuildMissingPublishTokenMessage(run.Target.PullRequestUrl));
        }

        var thread = run.Artifacts.InlineComments.FirstOrDefault(item => item.Id == commentId)
                     ?? throw new InvalidOperationException($"Inline comment '{commentId}' was not found.");

        if (!thread.IsRelevant)
        {
            throw new InvalidOperationException("Нельзя публиковать замечание, помеченное как неактуальное.");
        }

        if (thread.PublishedToTfs)
        {
            return run.ToDto();
        }

        var published = await reviewPublisher.PublishInlineCommentAsync(
            run.Target.PullRequestUrl,
            accessToken,
            thread,
            cancellationToken);

        if (!published)
        {
            throw new InvalidOperationException(BuildPublishFailureMessage(run.Target.PullRequestUrl, "inline comment"));
        }

        var updatedArtifacts = ReplaceInlineComment(
            run,
            thread with
            {
                PublishedToTfs = true,
                PublishedAt = DateTimeOffset.UtcNow
            },
            markdownReportBuilder);

        run.UpdateArtifacts(updatedArtifacts);
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
    }

    public async Task<ReviewRunDto> PublishReportAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        if (run.Target.Kind != ReviewTargetKind.PullRequest || string.IsNullOrWhiteSpace(run.Target.PullRequestUrl))
        {
            throw new InvalidOperationException("Только pull request ревью можно публиковать итоговый отчёт обратно в исходную платформу.");
        }

        if (string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport))
        {
            throw new InvalidOperationException("The final report is not ready yet.");
        }

        if (run.PublishSucceeded)
        {
            return run.ToDto();
        }

        var accessToken = ResolvePullRequestAccessToken(run.Target.PullRequestUrl, null);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(BuildMissingPublishTokenMessage(run.Target.PullRequestUrl));
        }

        var published = await reviewPublisher.PublishReportAsync(
            run.Target.PullRequestUrl,
            accessToken,
            run.Artifacts.MarkdownReport,
            cancellationToken);

        if (!published)
        {
            throw new InvalidOperationException(BuildPublishFailureMessage(run.Target.PullRequestUrl, "report"));
        }

        run.MarkPublishSucceeded();
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
    }

    public async Task<ReviewRunDto> SetInlineCommentRelevanceAsync(
        Guid runId,
        Guid commentId,
        bool isRelevant,
        CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        var thread = run.Artifacts.InlineComments.FirstOrDefault(item => item.Id == commentId)
                     ?? throw new InvalidOperationException($"Inline comment '{commentId}' was not found.");

        if (thread.IsRelevant == isRelevant)
        {
            return run.ToDto();
        }

        var updatedArtifacts = ReplaceInlineComment(
            run,
            thread with
            {
                IsRelevant = isRelevant
            },
            markdownReportBuilder);

        run.UpdateArtifacts(updatedArtifacts);
        run.UpdateFindings(BuildRelevantFindings(updatedArtifacts));
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
    }

    public async Task<ReviewRunDto> ContinueInlineDiscussionAsync(
        Guid runId,
        Guid commentId,
        ContinueInlineDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new InvalidOperationException("Discussion message cannot be empty.");
        }

        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");
        var thread = run.Artifacts.InlineComments.FirstOrDefault(item => item.Id == commentId)
                     ?? throw new InvalidOperationException($"Inline comment '{commentId}' was not found.");

        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            run.ProviderProfileId,
            [],
            cancellationToken);

        var file = run.Artifacts.ReviewedFiles.FirstOrDefault(item => PathsMatch(item.FilePath, thread.FilePath));
        var fileContent = file?.FullContent ?? string.Empty;
        var filePatch = file?.DiffPatch ?? string.Empty;
        var relevantDiffHunk = !string.IsNullOrWhiteSpace(thread.RelevantDiffHunk)
            ? thread.RelevantDiffHunk
            : ExtractRelevantDiffHunk(filePatch, thread);
        var history = string.Join(
            "\n\n",
            thread.Messages.Select(message => $"{message.Role.ToUpperInvariant()}:\n{message.Content}"));

        var assistantReply = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = 0.2,
                ExpectJson = true,
                SystemPrompt = """
                    You are a Principal .NET reviewer continuing an inline code review discussion.
                    Answer in Russian.
                    Stay grounded in the provided selected diff hunk, relevant code context, existing finding, and discussion history.
                    Give concrete, implementation-level advice.
                    Focus on the exact changed block under discussion rather than the whole file.
                    If the provided hunk or context is not enough, say exactly what is missing.
                    Return JSON only.
                    Do not return markdown.
                    All string values must be plain text without markdown emphasis markers such as **, __, or backticks.
                    Use this exact schema:
                    {
                      "summary": "short direct answer for the user",
                      "problems": ["specific problem or implication", "another problem if needed"],
                      "risk": "concrete risk or impact",
                      "recommendations": ["actionable recommendation", "second recommendation if needed"],
                      "shouldPublishToTfs": true,
                      "publishToTfsReason": "why this should or should not be published",
                      "exampleCodeLanguage": "csharp",
                      "exampleCode": "optional code example, otherwise empty string"
                    }
                    Keep arrays short.
                    If there is no code example, return an empty string in exampleCode and exampleCodeLanguage.
                    """,
                UserPrompt = $"""
                    Review target: {run.DisplayTitle}
                    File: {thread.FilePath}
                    Inline finding title: {thread.Title}
                    Severity: {thread.Severity}
                    Line number: {thread.LineNumber}

                    Initial inline comment:
                    {thread.Content}

                    Existing code snippet:
                    {thread.ExistingCode}

                    Relevant context block:
                    {thread.ContextBlock}

                    Relevant diff hunk:
                    {TrimForPrompt(relevantDiffHunk, 8000)}

                    Suggested fix:
                    {thread.Suggestion}

                    Additional file context (fallback only):
                    {TrimForPrompt(string.IsNullOrWhiteSpace(thread.ContextBlock) ? fileContent : string.Empty, 6000)}

                    Discussion history:
                    {TrimForPrompt(history, 12000)}

                    User question:
                    {request.Message}
                    """
            },
            cancellationToken);

        var structuredReply = ParseInlineDiscussionStructuredContent(assistantReply);
        var renderedReply = structuredReply is null
            ? assistantReply
            : RenderInlineDiscussionMarkdown(structuredReply);

        var updatedMessages = thread.Messages
            .Concat(
                [
                    new ReviewCommentMessage("user", request.Message.Trim(), DateTimeOffset.UtcNow),
                    new ReviewCommentMessage("assistant", renderedReply, DateTimeOffset.UtcNow, structuredReply)
                ])
            .ToArray();

        var updatedThread = thread with
        {
            Messages = updatedMessages
        };

        run.UpdateArtifacts(ReplaceInlineComment(run, updatedThread, markdownReportBuilder));
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
    }

    public async Task<ReviewRunDto> ContinueReviewDiscussionAsync(
        Guid runId,
        ContinueReviewDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new InvalidOperationException("Discussion message cannot be empty.");
        }

        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        var selection = await llmStageRouter.ResolveAsync(
            ReviewPipelineStage.ChunkReview,
            run.ProviderProfileId,
            [],
            cancellationToken);

        var history = string.Join(
            "\n\n",
            run.Artifacts.ReviewDiscussionMessages.Select(message => $"{message.Role.ToUpperInvariant()}:\n{message.Content}"));

        var assistantReply = await llmCompletionService.CompleteAsync(
            selection.Profile,
            new LlmChatRequest
            {
                Model = selection.Model,
                Temperature = 0.2,
                ExpectJson = true,
                SystemPrompt = """
                    You are a Principal .NET reviewer continuing a global review discussion for an already prepared code review.
                    Answer in Russian.
                    Stay grounded in the prepared diff, current findings, and discussion history.
                    Give concrete, implementation-level advice.
                    Focus on the user's follow-up request against the already prepared review context rather than re-summarizing the whole change.
                    If the user explicitly asks to search for additional defects, you may return new_findings, but only for concrete review-worthy issues.
                    Do not return style-only, cleanup-only, comment-only, or speculative findings.
                    If there are no new review-worthy issues, return an empty new_findings array.
                    If the provided review context is not enough, say exactly what is missing.
                    Return JSON only.
                    Do not return markdown.
                    All string values must be plain text without markdown emphasis markers such as **, __, or backticks.
                    Use this exact schema:
                    {
                      "summary": "short direct answer for the user",
                      "problems": ["specific problem or implication", "another problem if needed"],
                      "risk": "concrete risk or impact",
                      "recommendations": ["actionable recommendation", "second recommendation if needed"],
                      "shouldPublishToTfs": true,
                      "publishToTfsReason": "why this should or should not be published",
                      "exampleCodeLanguage": "csharp",
                      "exampleCode": "optional code example, otherwise empty string",
                      "new_findings": [
                        {
                          "kind": "Defect or Risk",
                          "file": "relative/path/to/file.cs",
                          "line_hint": "Line 123",
                          "start_line": 123,
                          "end_line": 126,
                          "type": "Bug or Reliability or Logic or Security or Performance or Architecture",
                          "severity": "Critical or High or Medium or Low",
                          "title": "short issue title",
                          "description": "why this is a problem",
                          "existing_code": "problematic changed code only",
                          "suggestion": "minimal concrete fix"
                        }
                      ]
                    }
                    Keep arrays short.
                    If there is no code example, return an empty string in exampleCode and exampleCodeLanguage.
                    """,
                UserPrompt = $"""
                    Review target: {run.DisplayTitle}

                    Current findings:
                    {TrimForPrompt(BuildFindingsSummary(run), 10000)}

                    Prepared review diff:
                    {TrimForPrompt(run.Artifacts.DiffText, 22000)}

                    Discussion history:
                    {TrimForPrompt(history, 12000)}

                    User question:
                    {request.Message}
                    """
            },
            cancellationToken);

        var parsedResult = ParseReviewDiscussionResult(assistantReply, findingsNormalizer);
        var uniqueNewFindings = GetUniqueNewFindings(run.Findings, parsedResult.NewFindings);
        var structuredReply = WithAddedFindingsCount(
            parsedResult.StructuredContent,
            uniqueNewFindings.Count,
            BuildAddedFindingSummaries(uniqueNewFindings));
        var renderedReply = structuredReply is null
            ? assistantReply
            : RenderInlineDiscussionMarkdown(structuredReply);

        var updatedMessages = run.Artifacts.ReviewDiscussionMessages
            .Concat(
                [
                    new ReviewCommentMessage("user", request.Message.Trim(), DateTimeOffset.UtcNow),
                    new ReviewCommentMessage("assistant", renderedReply, DateTimeOffset.UtcNow, structuredReply)
                ])
            .ToArray();
        var mergedFindings = uniqueNewFindings.Count == 0
            ? run.Findings
            : run.Findings.Concat(uniqueNewFindings).ToArray();
        var mergedInlineComments = uniqueNewFindings.Count == 0
            ? run.Artifacts.InlineComments
            : MergeInlineComments(run.Artifacts, uniqueNewFindings, markdownReportBuilder);
        var mergedReviewedFiles = uniqueNewFindings.Count == 0
            ? run.Artifacts.ReviewedFiles
            : MergeReviewedFiles(run.Artifacts.ReviewedFiles, mergedInlineComments);
        var relevantFindings = mergedInlineComments.Count > 0
            ? BuildRelevantFindings(new ReviewArtifacts { InlineComments = mergedInlineComments })
            : mergedFindings;
        var findingsComparison = await RebuildFindingsComparisonAsync(run, mergedFindings, cancellationToken);

        run.UpdateFindings(mergedFindings);
        run.UpdateArtifacts(new ReviewArtifacts
        {
            DiffText = run.Artifacts.DiffText,
            ChangedFiles = run.Artifacts.ChangedFiles,
            ChangeDescription = run.Artifacts.ChangeDescription,
            ChangeDescriptionStructured = run.Artifacts.ChangeDescriptionStructured,
            ChangeDiagramMermaid = run.Artifacts.ChangeDiagramMermaid,
            MarkdownReport = markdownReportBuilder.BuildFullReport(
                run.DisplayTitle,
                run.Artifacts.ChangeDescription,
                relevantFindings,
                findingsComparison),
            SummaryComment = markdownReportBuilder.BuildSummaryComment(
                run.DisplayTitle,
                run.Artifacts.ChangeDescription,
                relevantFindings,
                findingsComparison),
            ReviewDiscussionMessages = updatedMessages,
            InlineComments = mergedInlineComments,
            ReviewedFiles = mergedReviewedFiles,
            FindingsComparison = findingsComparison
        });

        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
    }

    private static InlineDiscussionStructuredContent? ParseInlineDiscussionStructuredContent(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return null;
        }

        try
        {
            var json = ExtractJsonObject(rawResponse);
            using var document = JsonDocument.Parse(json);
            return ParseInlineDiscussionStructuredContent(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RenderInlineDiscussionMarkdown(InlineDiscussionStructuredContent content)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(content.Summary))
        {
            builder.AppendLine("### Пояснение");
            builder.AppendLine(content.Summary.Trim());
            builder.AppendLine();
        }

        if (content.Problems.Count > 0)
        {
            builder.AppendLine("### Проблема");
            for (var index = 0; index < content.Problems.Count; index++)
            {
                builder.Append(index + 1).Append(". ").AppendLine(content.Problems[index]);
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(content.Risk))
        {
            builder.AppendLine("### Риск");
            builder.AppendLine(content.Risk.Trim());
            builder.AppendLine();
        }

        if (content.Recommendations.Count > 0)
        {
            builder.AppendLine("### Рекомендации");
            for (var index = 0; index < content.Recommendations.Count; index++)
            {
                builder.Append(index + 1).Append(". ").AppendLine(content.Recommendations[index]);
            }

            builder.AppendLine();
        }

        if (content.ShouldPublishToTfs.HasValue || !string.IsNullOrWhiteSpace(content.PublishToTfsReason))
        {
            builder.AppendLine("### Публикация в pull request");
            if (content.ShouldPublishToTfs.HasValue)
            {
                builder.AppendLine(content.ShouldPublishToTfs.Value ? "Да" : "Нет");
            }

            if (!string.IsNullOrWhiteSpace(content.PublishToTfsReason))
            {
                builder.AppendLine(content.PublishToTfsReason.Trim());
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(content.ExampleCode))
        {
            builder.AppendLine("### Пример кода");
            builder.Append("```");
            builder.AppendLine(content.ExampleCodeLanguage);
            builder.AppendLine(content.ExampleCode);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (content.AddedFindingsCount > 0)
        {
            builder.AppendLine("### Новые замечания");
            builder.AppendLine($"LLM добавил замечаний: {content.AddedFindingsCount}");
            if (content.AddedFindings.Count > 0)
            {
                builder.AppendLine();
                foreach (var addedFinding in content.AddedFindings)
                {
                    builder.AppendLine($"- {addedFinding}");
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string ExtractJsonObject(string rawResponse)
    {
        var trimmed = rawResponse.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
            {
                trimmed = trimmed[(firstNewLine + 1)..];
            }

            var closingFenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFenceIndex >= 0)
            {
                trimmed = trimmed[..closingFenceIndex];
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return trimmed;
    }

    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string>? items)
    {
        return items?
            .Select(item => item?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray()
            ?? [];
    }

    private static InlineDiscussionStructuredContent? ParseInlineDiscussionStructuredContent(
        JsonElement root,
        int addedFindingsCount = 0,
        IReadOnlyList<string>? addedFindings = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<InlineDiscussionResponsePayload>(root.GetRawText(), InlineDiscussionJsonOptions);
        if (payload is null)
        {
            return null;
        }

        var problems = NormalizeList(payload.Problems);
        var recommendations = NormalizeList(payload.Recommendations);
        var summary = payload.Summary?.Trim() ?? string.Empty;
        var risk = payload.Risk?.Trim() ?? string.Empty;
        var publishReason = payload.PublishToTfsReason?.Trim() ?? string.Empty;
        var exampleCode = payload.ExampleCode?.Trim() ?? string.Empty;
        var exampleLanguage = payload.ExampleCodeLanguage?.Trim() ?? string.Empty;
        var addedFindingItems = addedFindings ?? [];

        if (string.IsNullOrWhiteSpace(summary)
            && problems.Count == 0
            && string.IsNullOrWhiteSpace(risk)
            && recommendations.Count == 0
            && string.IsNullOrWhiteSpace(publishReason)
            && string.IsNullOrWhiteSpace(exampleCode)
            && addedFindingsCount == 0
            && addedFindingItems.Count == 0)
        {
            return null;
        }

        return new InlineDiscussionStructuredContent
        {
            Summary = summary,
            Problems = problems,
            Risk = risk,
            Recommendations = recommendations,
            ShouldPublishToTfs = payload.ShouldPublishToTfs,
            PublishToTfsReason = publishReason,
            ExampleCodeLanguage = exampleLanguage,
            ExampleCode = exampleCode,
            AddedFindingsCount = addedFindingsCount,
            AddedFindings = addedFindingItems
        };
    }

    private static ParsedReviewDiscussionResult ParseReviewDiscussionResult(
        string rawResponse,
        IFindingsNormalizer findingsNormalizer)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return new ParsedReviewDiscussionResult(null, []);
        }

        try
        {
            var json = ExtractJsonObject(rawResponse);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var newFindings = root.TryGetProperty("new_findings", out var newFindingsNode) &&
                              newFindingsNode.ValueKind == JsonValueKind.Array
                ? findingsNormalizer.Normalize([newFindingsNode.GetRawText()])
                    .Select(finding => finding with { Source = ReviewFindingSource.FollowUpDiscussion })
                    .ToArray()
                : [];

            var structuredContent = ParseInlineDiscussionStructuredContent(
                root,
                newFindings.Length,
                BuildAddedFindingSummaries(newFindings));
            return new ParsedReviewDiscussionResult(structuredContent, newFindings);
        }
        catch (JsonException)
        {
            return new ParsedReviewDiscussionResult(ParseInlineDiscussionStructuredContent(rawResponse), []);
        }
    }

    private sealed class InlineDiscussionResponsePayload
    {
        public string? Summary { get; init; }

        public IReadOnlyList<string>? Problems { get; init; }

        public string? Risk { get; init; }

        public IReadOnlyList<string>? Recommendations { get; init; }

        public bool? ShouldPublishToTfs { get; init; }

        public string? PublishToTfsReason { get; init; }

        public string? ExampleCodeLanguage { get; init; }

        public string? ExampleCode { get; init; }
    }

    private sealed record ParsedReviewDiscussionResult(
        InlineDiscussionStructuredContent? StructuredContent,
        IReadOnlyList<ReviewFinding> NewFindings);

    private static InlineDiscussionStructuredContent? WithAddedFindingsCount(
        InlineDiscussionStructuredContent? content,
        int addedFindingsCount,
        IReadOnlyList<string>? addedFindings = null)
    {
        if (content is null)
        {
            return addedFindingsCount <= 0
                ? null
                : new InlineDiscussionStructuredContent
                {
                    AddedFindingsCount = addedFindingsCount,
                    AddedFindings = addedFindings ?? []
                };
        }

        return new InlineDiscussionStructuredContent
        {
            Summary = content.Summary,
            Problems = content.Problems,
            Risk = content.Risk,
            Recommendations = content.Recommendations,
            ShouldPublishToTfs = content.ShouldPublishToTfs,
            PublishToTfsReason = content.PublishToTfsReason,
            ExampleCodeLanguage = content.ExampleCodeLanguage,
            ExampleCode = content.ExampleCode,
            AddedFindingsCount = addedFindingsCount,
            AddedFindings = addedFindings ?? content.AddedFindings
        };
    }

    private static IReadOnlyList<string> BuildAddedFindingSummaries(IReadOnlyList<ReviewFinding> findings)
    {
        return findings
            .Select(finding =>
            {
                var location = finding.StartLine > 0
                    ? $" (строка {finding.StartLine})"
                    : string.Empty;
                return $"{finding.File}{location}: {finding.Title}";
            })
            .ToArray();
    }

    public async Task<ArtifactDownloadResult?> GetDiffDownloadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken);
        if (run is null || string.IsNullOrWhiteSpace(run.Artifacts.DiffText))
        {
            return null;
        }

        return new ArtifactDownloadResult
        {
            FileName = $"{BuildArtifactFileStem(run)}.txt",
            ContentType = "text/plain; charset=utf-8",
            Content = System.Text.Encoding.UTF8.GetBytes(run.Artifacts.DiffText)
        };
    }

    public async Task<ArtifactDownloadResult?> GetMarkdownReportDownloadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken);
        if (run is null || string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport))
        {
            return null;
        }

        return new ArtifactDownloadResult
        {
            FileName = $"{BuildArtifactFileStem(run, "review")}.md",
            ContentType = "text/markdown; charset=utf-8",
            Content = System.Text.Encoding.UTF8.GetBytes(run.Artifacts.MarkdownReport)
        };
    }

    private async Task<ReviewRunDto> EnqueueAsync(
        ReviewTargetDescriptor target,
        ReviewExecutionRequest executionRequest,
        CancellationToken cancellationToken)
    {
        var run = new ReviewRun(Guid.NewGuid(), target, executionRequest.ProviderProfileId);
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

    private static string? ResolvePullRequestAccessToken(string? pullRequestUrl, string? requestToken)
    {
        return !string.IsNullOrWhiteSpace(requestToken)
            ? requestToken
            : PullRequestPlatformDetector.Detect(pullRequestUrl) switch
            {
                PullRequestPlatformKind.AzureDevOps => Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN"),
                PullRequestPlatformKind.GitHub => Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                PullRequestPlatformKind.GitLab => Environment.GetEnvironmentVariable("GITLAB_TOKEN"),
                _ => null
            };
    }

    private static string BuildMissingPublishTokenMessage(string? pullRequestUrl)
    {
        return PullRequestPlatformDetector.Detect(pullRequestUrl) switch
        {
            PullRequestPlatformKind.GitHub => "GITHUB_TOKEN требуется для публикации замечаний или итогового отчёта в GitHub.",
            PullRequestPlatformKind.GitLab => "GITLAB_TOKEN требуется для публикации замечаний или итогового отчёта в GitLab.",
            PullRequestPlatformKind.AzureDevOps => "AZURE_DEVOPS_TOKEN требуется для публикации замечаний или итогового отчёта в Azure DevOps/TFS.",
            _ => "Требуется токен доступа для публикации замечаний или итогового отчёта обратно в pull request."
        };
    }

    private static string BuildPublishFailureMessage(string? pullRequestUrl, string artifactType)
    {
        var subject = artifactType switch
        {
            "inline comment" => "замечание",
            "report" => "итоговый отчёт",
            _ => artifactType
        };

        return PullRequestPlatformDetector.Detect(pullRequestUrl) switch
        {
            PullRequestPlatformKind.GitHub => $"Не удалось опубликовать {subject} в GitHub.",
            PullRequestPlatformKind.GitLab => $"Не удалось опубликовать {subject} в GitLab.",
            PullRequestPlatformKind.AzureDevOps => $"Не удалось опубликовать {subject} в Azure DevOps/TFS.",
            _ => $"Не удалось опубликовать {subject} в pull request платформу."
        };
    }

    private static ReviewArtifacts ReplaceInlineComment(
        ReviewRun run,
        InlineCommentDraft updatedThread,
        IMarkdownReportBuilder markdownReportBuilder)
    {
        var artifacts = run.Artifacts;
        var inlineComments = GetMaterializedInlineComments(artifacts)
            .Select(comment => comment.Id == updatedThread.Id ? updatedThread : comment)
            .ToArray();

        var reviewedFiles = artifacts.ReviewedFiles
            .Select(file => new ReviewedFileArtifact
            {
                FilePath = file.FilePath,
                DisplayName = file.DisplayName,
                ChangeType = file.ChangeType,
                AddedLines = file.AddedLines,
                DeletedLines = file.DeletedLines,
                DiffPatch = file.DiffPatch,
                FullContent = file.FullContent,
                ChangedLineNumbers = file.ChangedLineNumbers,
                InlineThreads = file.InlineThreads.Select(comment => comment.Id == updatedThread.Id ? updatedThread : comment).ToArray()
            })
            .ToArray();

        var provisionalArtifacts = new ReviewArtifacts
        {
            DiffText = artifacts.DiffText,
            ChangedFiles = artifacts.ChangedFiles,
            ChangeDescription = artifacts.ChangeDescription,
            ChangeDescriptionStructured = artifacts.ChangeDescriptionStructured,
            ChangeDiagramMermaid = artifacts.ChangeDiagramMermaid,
            MarkdownReport = artifacts.MarkdownReport,
            SummaryComment = artifacts.SummaryComment,
            ReviewDiscussionMessages = artifacts.ReviewDiscussionMessages,
            InlineComments = inlineComments,
            ReviewedFiles = reviewedFiles,
            FindingsComparison = artifacts.FindingsComparison
        };

        var relevantFindings = BuildRelevantFindings(provisionalArtifacts);
        var relevantComparison = FilterComparison(artifacts.FindingsComparison, relevantFindings);

        return new ReviewArtifacts
        {
            DiffText = artifacts.DiffText,
            ChangedFiles = artifacts.ChangedFiles,
            ChangeDescription = artifacts.ChangeDescription,
            ChangeDescriptionStructured = artifacts.ChangeDescriptionStructured,
            ChangeDiagramMermaid = artifacts.ChangeDiagramMermaid,
            MarkdownReport = markdownReportBuilder.BuildFullReport(
                run.DisplayTitle,
                artifacts.ChangeDescription,
                relevantFindings,
                relevantComparison),
            SummaryComment = markdownReportBuilder.BuildSummaryComment(
                run.DisplayTitle,
                artifacts.ChangeDescription,
                relevantFindings,
                relevantComparison),
            ReviewDiscussionMessages = artifacts.ReviewDiscussionMessages,
            InlineComments = inlineComments,
            ReviewedFiles = reviewedFiles,
            FindingsComparison = relevantComparison
        };
    }

    private static IReadOnlyList<ReviewFinding> BuildRelevantFindings(ReviewArtifacts artifacts)
    {
        return GetMaterializedInlineComments(artifacts)
            .Where(comment => comment.IsRelevant)
            .Select(MapFindingFromInlineComment)
            .OrderBy(finding => finding.Severity)
            .ToArray();
    }

    private static ReviewFinding MapFindingFromInlineComment(InlineCommentDraft comment)
    {
        var file = comment.FilePath.TrimStart('/');
        var lineHint = comment.StartLine > 0
            ? $"Line {comment.StartLine}"
            : comment.LineNumber > 0
                ? $"Line {comment.LineNumber}"
                : "Unknown";
        var description = ExtractFindingDescription(comment);

        return new ReviewFinding(
            file,
            lineHint,
            Enum.TryParse<FindingCategory>(comment.Category, true, out var category) ? category : FindingCategory.Bug,
            Enum.TryParse<FindingSeverity>(comment.Severity, true, out var severity) ? severity : FindingSeverity.Medium,
            ParseFindingSource(comment.Source),
            comment.Title,
            description,
            comment.ExistingCode,
            comment.Suggestion,
            comment.StartLine > 0 ? comment.StartLine : comment.LineNumber,
            comment.EndLine > 0 ? comment.EndLine : (comment.StartLine > 0 ? comment.StartLine : comment.LineNumber),
            comment.FindingId);
    }

    private async Task<FindingsComparisonSnapshot?> RebuildFindingsComparisonAsync(
        ReviewRun run,
        IReadOnlyList<ReviewFinding> mergedFindings,
        CancellationToken cancellationToken)
    {
        var previousRunId = run.Artifacts.FindingsComparison?.PreviousRunId;
        if (previousRunId is null)
        {
            return run.Artifacts.FindingsComparison;
        }

        var previousRun = await reviewRunRepository.GetAsync(previousRunId.Value, cancellationToken);
        return previousRun is null
            ? run.Artifacts.FindingsComparison
            : findingsComparisonService.Compare(previousRun.Id, previousRun.Findings, mergedFindings);
    }

    private static IReadOnlyList<ReviewFinding> GetUniqueNewFindings(
        IReadOnlyList<ReviewFinding> existingFindings,
        IReadOnlyList<ReviewFinding> candidateFindings)
    {
        if (candidateFindings.Count == 0)
        {
            return [];
        }

        var existingKeys = existingFindings
            .Select(BuildSemanticFindingKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidateFindings
            .Where(finding => existingKeys.Add(BuildSemanticFindingKey(finding)))
            .ToArray();
    }

    private static IReadOnlyList<InlineCommentDraft> MergeInlineComments(
        ReviewArtifacts artifacts,
        IReadOnlyList<ReviewFinding> newFindings,
        IMarkdownReportBuilder markdownReportBuilder)
    {
        if (newFindings.Count == 0)
        {
            return GetMaterializedInlineComments(artifacts);
        }

        var newComments = markdownReportBuilder.BuildInlineComments(newFindings, artifacts.DiffText)
            .Select(comment => EnrichInlineComment(comment, artifacts.ReviewedFiles))
            .ToArray();

        return GetMaterializedInlineComments(artifacts)
            .Concat(newComments)
            .OrderBy(comment => NormalizePath(comment.FilePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(comment => comment.LineNumber)
            .ThenBy(comment => comment.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<InlineCommentDraft> GetMaterializedInlineComments(ReviewArtifacts artifacts)
    {
        if (artifacts.ReviewedFiles.Count > 0)
        {
            return artifacts.ReviewedFiles
                .SelectMany(file => file.InlineThreads)
                .OrderBy(comment => NormalizePath(comment.FilePath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(comment => comment.LineNumber)
                .ThenBy(comment => comment.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return artifacts.InlineComments;
    }

    private static IReadOnlyList<ReviewedFileArtifact> MergeReviewedFiles(
        IReadOnlyList<ReviewedFileArtifact> reviewedFiles,
        IReadOnlyList<InlineCommentDraft> inlineComments)
    {
        return reviewedFiles
            .Select(file => new ReviewedFileArtifact
            {
                FilePath = file.FilePath,
                DisplayName = file.DisplayName,
                ChangeType = file.ChangeType,
                AddedLines = file.AddedLines,
                DeletedLines = file.DeletedLines,
                DiffPatch = file.DiffPatch,
                FullContent = file.FullContent,
                ChangedLineNumbers = file.ChangedLineNumbers,
                InlineThreads = inlineComments
                    .Where(comment => PathsMatch(comment.FilePath, file.FilePath))
                    .OrderBy(comment => comment.LineNumber)
                    .ThenBy(comment => comment.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToArray();
    }

    private static InlineCommentDraft EnrichInlineComment(
        InlineCommentDraft thread,
        IReadOnlyList<ReviewedFileArtifact> reviewedFiles)
    {
        var file = reviewedFiles.FirstOrDefault(item => PathsMatch(item.FilePath, thread.FilePath));
        if (file is null)
        {
            return thread;
        }

        var normalizedThread = NormalizeThreadLocation(thread, file.FullContent);
        var context = GlobalContextExtractor.Build(
            normalizedThread.LineNumber,
            normalizedThread.StartLine,
            normalizedThread.EndLine,
            file.FullContent,
            file.DiffPatch,
            normalizedThread.ExistingCode);
        var provisionalThread = normalizedThread with
        {
            ContextBlock = context.Block,
            ContextStartLine = context.StartLine,
            ContextEndLine = context.EndLine
        };

        return provisionalThread with
        {
            RelevantDiffHunk = ExtractRelevantDiffHunk(file.DiffPatch, provisionalThread)
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
        var snippetRange = GlobalContextExtractor.TryLocateSnippetRangeInFile(
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

    private static string BuildSemanticFindingKey(ReviewFinding finding)
    {
        return string.Join(
            "|",
            NormalizePath(finding.File),
            finding.Title.Trim(),
            finding.StartLine.ToString(CultureInfo.InvariantCulture),
            finding.EndLine.ToString(CultureInfo.InvariantCulture),
            NormalizeMatchValue(finding.ExistingCode));
    }

    private static string NormalizeMatchValue(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray())
            .Trim()
            .ToLowerInvariant();
    }

    private static ReviewFindingSource ParseFindingSource(string? source)
    {
        return Enum.TryParse<ReviewFindingSource>(source, true, out var parsed)
            ? parsed
            : ReviewFindingSource.InitialReview;
    }

    private static class GlobalContextExtractor
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
    }

    private static string BuildFindingsSummary(ReviewRun run)
    {
        var findings = run.Artifacts.InlineComments.Count > 0
            ? BuildRelevantFindings(run.Artifacts)
            : run.Findings;

        if (findings.Count == 0)
        {
            return "<no findings>";
        }

        return string.Join(
            "\n",
            findings.Select((finding, index) =>
                $"{index + 1}. [{finding.Severity}] {finding.File}:{finding.StartLine}-{finding.EndLine} | {finding.Title} | {finding.Description}"));
    }

    private static string ExtractFindingDescription(InlineCommentDraft comment)
    {
        var initialAssistantMessage = comment.Messages.FirstOrDefault(message => message.Role == "assistant");
        if (!string.IsNullOrWhiteSpace(initialAssistantMessage?.StructuredContent?.Summary))
        {
            return initialAssistantMessage.StructuredContent.Summary.Trim();
        }

        if (!string.IsNullOrWhiteSpace(comment.Content))
        {
            var parts = comment.Content.Split("\n\n", 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? parts[1].Trim() : comment.Content.Trim();
        }

        return string.Empty;
    }

    private static FindingsComparisonSnapshot? FilterComparison(
        FindingsComparisonSnapshot? comparison,
        IReadOnlyList<ReviewFinding> relevantFindings)
    {
        if (comparison is null)
        {
            return null;
        }

        var relevantKeys = relevantFindings
            .Select(BuildFindingKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newFindings = comparison.NewFindings
            .Where(finding => relevantKeys.Contains(BuildFindingKey(finding)))
            .ToArray();

        var stillRelevantFindings = comparison.StillRelevantFindings
            .Where(finding => relevantKeys.Contains(BuildFindingKey(finding)))
            .ToArray();

        return new FindingsComparisonSnapshot
        {
            PreviousRunId = comparison.PreviousRunId,
            PreviousFindingsCount = comparison.PreviousFindingsCount,
            CurrentFindingsCount = relevantFindings.Count,
            NewFindingsCount = newFindings.Length,
            StillRelevantFindingsCount = stillRelevantFindings.Length,
            ResolvedFindingsCount = comparison.ResolvedFindingsCount,
            NewFindings = newFindings,
            StillRelevantFindings = stillRelevantFindings,
            ResolvedFindings = comparison.ResolvedFindings
        };
    }

    private static string BuildFindingKey(ReviewFinding finding)
    {
        if (finding.Id != Guid.Empty)
        {
            return finding.Id.ToString("N");
        }

        return $"{NormalizePath(finding.File)}|{finding.Title.Trim()}|{finding.StartLine}|{finding.EndLine}";
    }

    private static bool PathsMatch(string left, string right)
    {
        return NormalizePath(left) == NormalizePath(right);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).TrimStart('/').ToLowerInvariant();
    }

    private static string ExtractRelevantDiffHunk(string filePatch, InlineCommentDraft thread)
    {
        if (string.IsNullOrWhiteSpace(filePatch))
        {
            return "<empty>";
        }

        var normalizedPatch = filePatch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalizedPatch.Split('\n');
        var hunks = ParseHunks(lines);
        if (hunks.Count == 0)
        {
            return TrimForPrompt(filePatch, 8000);
        }

        var byLineNumber = hunks.FirstOrDefault(hunk =>
            thread.LineNumber > 0 &&
            hunk.NewLineStart <= thread.LineNumber &&
            thread.LineNumber <= hunk.NewLineEnd);
        if (byLineNumber is not null)
        {
            return byLineNumber.Content;
        }

        var byContextRange = hunks.FirstOrDefault(hunk =>
            thread.ContextStartLine > 0 &&
            thread.ContextEndLine > 0 &&
            RangesOverlap(hunk.NewLineStart, hunk.NewLineEnd, thread.ContextStartLine, thread.ContextEndLine));
        if (byContextRange is not null)
        {
            return byContextRange.Content;
        }

        var bySnippet = hunks
            .Select(hunk => new
            {
                Hunk = hunk,
                Score = ComputeSnippetScore(hunk.Content, thread.ExistingCode)
            })
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (bySnippet is not null && bySnippet.Score >= 0.84d)
        {
            return bySnippet.Hunk.Content;
        }

        return hunks[0].Content;
    }

    private static string TrimForPrompt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        return text.Length <= maxLength ? text : $"{text[..maxLength]}...";
    }

    private static bool RangesOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd)
    {
        return Math.Max(leftStart, rightStart) <= Math.Min(leftEnd, rightEnd);
    }

    private static double ComputeSnippetScore(string hunk, string snippet)
    {
        if (string.IsNullOrWhiteSpace(hunk) || string.IsNullOrWhiteSpace(snippet))
        {
            return 0d;
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
            return 0d;
        }

        var bestScore = 0d;
        foreach (var hunkLine in hunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!(hunkLine.StartsWith("+", StringComparison.Ordinal) || hunkLine.StartsWith(" ", StringComparison.Ordinal)))
            {
                continue;
            }

            var normalizedHunkLine = NormalizeForMatch(hunkLine[1..]);
            foreach (var candidate in snippetCandidates)
            {
                var score = ComputeSimilarity(normalizedHunkLine, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                }
            }
        }

        return bestScore;
    }

    private static double ComputeSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            return 0.95d;
        }

        var distance = ComputeLevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        return maxLength == 0 ? 0d : 1d - ((double)distance / maxLength);
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j <= right.Length; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + substitutionCost);
            }
        }

        return matrix[left.Length, right.Length];
    }

    private static string NormalizeForMatch(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray())
            .Trim()
            .TrimStart('+')
            .ToLowerInvariant();
    }

    private static IReadOnlyList<DiffHunk> ParseHunks(IReadOnlyList<string> lines)
    {
        var result = new List<DiffHunk>();
        DiffHunk? currentHunk = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (currentHunk is not null)
                {
                    result.Add(currentHunk with { Content = string.Join('\n', currentLines) });
                    currentLines = new List<string>();
                }

                var parsed = ParseHunkHeader(line);
                currentHunk = new DiffHunk(parsed.NewLineStart, parsed.NewLineEnd, string.Empty);
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
        var match = Regex.Match(
            header,
            @"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return (1, 1);
        }

        var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        var countGroup = match.Groups["count"];
        var count = countGroup.Success && int.TryParse(countGroup.Value, out var parsedCount)
            ? parsedCount
            : 1;
        var end = Math.Max(start, start + Math.Max(count - 1, 0));
        return (start, end);
    }

    private sealed record DiffHunk(int NewLineStart, int NewLineEnd, string Content);

    private static string BuildArtifactFileStem(ReviewRun run, string prefix = "diff")
    {
        var timestamp = run.CreatedAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var target = run.Target;

        if (target.Kind == ReviewTargetKind.PullRequest && !string.IsNullOrWhiteSpace(target.PullRequestUrl))
        {
            var pullRequestMatch = Regex.Match(
                target.PullRequestUrl,
                @"_git/(?<repo>[^/]+)/pullrequest/(?<id>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (pullRequestMatch.Success)
            {
                var repo = SanitizeFileNamePart(Uri.UnescapeDataString(pullRequestMatch.Groups["repo"].Value));
                var pullRequestId = SanitizeFileNamePart(pullRequestMatch.Groups["id"].Value);
                return $"{prefix}_pr_{repo}_{pullRequestId}_{timestamp}";
            }
        }

        var repositoryName = SanitizeFileNamePart(target.RepositoryName ?? target.Title);
        if (target.Kind == ReviewTargetKind.BranchComparison)
        {
            var source = SanitizeFileNamePart(target.SourceBranch ?? "source");
            var destination = SanitizeFileNamePart(target.TargetBranch ?? "target");
            return $"{prefix}_branches_{repositoryName}_{source}_to_{destination}_{timestamp}";
        }

        return $"{prefix}_{repositoryName}_{timestamp}";
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value
                .Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) || ch is '/' or '\\' ? '_' : ch)
                .ToArray())
            .Trim('_');

        while (sanitized.Contains("__", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "review" : sanitized;
    }
}
