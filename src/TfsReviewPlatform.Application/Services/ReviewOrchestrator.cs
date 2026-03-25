using Microsoft.Extensions.Logging;
using System.Globalization;
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
    ILlmStageRouter llmStageRouter,
    ILlmCompletionService llmCompletionService,
    IReviewPublisher reviewPublisher,
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

    public async Task<ReviewRunDto> PublishInlineCommentAsync(Guid runId, Guid commentId, CancellationToken cancellationToken)
    {
        var run = await reviewRunRepository.GetAsync(runId, cancellationToken)
                  ?? throw new InvalidOperationException($"Review run '{runId}' was not found.");

        if (run.Target.Kind != ReviewTargetKind.PullRequest || string.IsNullOrWhiteSpace(run.Target.PullRequestUrl))
        {
            throw new InvalidOperationException("Only pull request reviews can publish inline comments to TFS.");
        }

        var accessToken = ResolveAzureDevOpsToken(null);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("AZURE_DEVOPS_TOKEN is required to publish an inline comment to TFS.");
        }

        var thread = run.Artifacts.InlineComments.FirstOrDefault(item => item.Id == commentId)
                     ?? throw new InvalidOperationException($"Inline comment '{commentId}' was not found.");

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
            throw new InvalidOperationException("Failed to publish the inline comment to TFS.");
        }

        var updatedArtifacts = ReplaceInlineComment(
            run.Artifacts,
            thread with
            {
                PublishedToTfs = true,
                PublishedAt = DateTimeOffset.UtcNow
            });

        run.UpdateArtifacts(updatedArtifacts);
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
            run.LocalOnlyMode,
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
                SystemPrompt = """
                    You are a Principal .NET reviewer continuing an inline code review discussion.
                    Answer in Russian.
                    Stay grounded in the provided selected diff hunk, relevant code context, existing finding, and discussion history.
                    Give concrete, implementation-level advice.
                    Focus on the exact changed block under discussion rather than the whole file.
                    If the provided hunk or context is not enough, say exactly what is missing.
                    If the user asks whether the comment should be published to TFS, answer directly.
                    """,
                UserPrompt = $"""
                    Review target: {run.Target.Title}
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

        var updatedMessages = thread.Messages
            .Concat(
                [
                    new ReviewCommentMessage("user", request.Message.Trim(), DateTimeOffset.UtcNow),
                    new ReviewCommentMessage("assistant", assistantReply, DateTimeOffset.UtcNow)
                ])
            .ToArray();

        var updatedThread = thread with
        {
            Messages = updatedMessages
        };

        run.UpdateArtifacts(ReplaceInlineComment(run.Artifacts, updatedThread));
        await reviewRunRepository.UpdateAsync(run, cancellationToken);
        return run.ToDto();
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

    private static ReviewArtifacts ReplaceInlineComment(ReviewArtifacts artifacts, InlineCommentDraft updatedThread)
    {
        var inlineComments = artifacts.InlineComments
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

        return new ReviewArtifacts
        {
            DiffText = artifacts.DiffText,
            ChangedFiles = artifacts.ChangedFiles,
            ChangeDescription = artifacts.ChangeDescription,
            ChangeDiagramMermaid = artifacts.ChangeDiagramMermaid,
            MarkdownReport = artifacts.MarkdownReport,
            SummaryComment = artifacts.SummaryComment,
            InlineComments = inlineComments,
            ReviewedFiles = reviewedFiles
        };
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
