using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public static class ReviewRunMappingExtensions
{
    public static ReviewHistoryDto ToHistoryDto(
        this IReadOnlyList<ReviewRun> runs,
        Guid? baselineRunId)
    {
        return new ReviewHistoryDto
        {
            BaselineRunId = baselineRunId,
            Items = runs.Select(ToHistoryItemDto).ToArray()
        };
    }

    public static ReviewRunDto ToDto(this ReviewRun run)
    {
        return new ReviewRunDto
        {
            Id = run.Id,
            Status = run.Status,
            TargetKind = run.Target.Kind,
            Title = run.DisplayTitle,
            PullRequestUrl = run.Target.PullRequestUrl,
            RepositoryName = run.Target.RepositoryName,
            SourceBranch = run.Target.SourceBranch,
            TargetBranch = run.Target.TargetBranch,
            ProviderProfileId = run.ProviderProfileId,
            ServiceName = run.ServiceName,
            AuthorName = run.AuthorName,
            CurrentStage = run.CurrentStage,
            ProgressPercent = run.ProgressPercent,
            CurrentMessage = run.CurrentMessage,
            ErrorMessage = run.ErrorMessage,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            ChangeDescription = run.Artifacts.ChangeDescription,
            ChangeDescriptionStructured = MapChangeDescription(run.Artifacts.ChangeDescriptionStructured),
            ChangeDiagramMermaid = MermaidDiagramNormalizer.Normalize(run.Artifacts.ChangeDiagramMermaid),
            HasDiffArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.DiffText),
            MarkdownReport = run.Artifacts.MarkdownReport,
            HasMarkdownReportArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport),
            SummaryComment = run.Artifacts.SummaryComment,
            PublishSucceeded = run.PublishSucceeded,
            ChangedFiles = run.Artifacts.ChangedFiles,
            Findings = run.Findings.Select(MapFinding).ToArray(),
            PrimaryOpportunities = run.Artifacts.PrimaryOpportunities.Select(MapOpportunity).ToArray(),
            FindingsComparison = run.Artifacts.FindingsComparison is null
                ? null
                : new FindingsComparisonDto
                {
                    PreviousRunId = run.Artifacts.FindingsComparison.PreviousRunId,
                    PreviousFindingsCount = run.Artifacts.FindingsComparison.PreviousFindingsCount,
                    CurrentFindingsCount = run.Artifacts.FindingsComparison.CurrentFindingsCount,
                    NewFindingsCount = run.Artifacts.FindingsComparison.NewFindingsCount,
                    StillRelevantFindingsCount = run.Artifacts.FindingsComparison.StillRelevantFindingsCount,
                    ResolvedFindingsCount = run.Artifacts.FindingsComparison.ResolvedFindingsCount,
                    IsDiffUnchanged = run.Artifacts.FindingsComparison.IsDiffUnchanged,
                    NewFindings = run.Artifacts.FindingsComparison.NewFindings.Select(MapFinding).ToArray(),
                    StillRelevantFindings = run.Artifacts.FindingsComparison.StillRelevantFindings.Select(MapFinding).ToArray(),
                    ResolvedFindings = run.Artifacts.FindingsComparison.ResolvedFindings.Select(MapFinding).ToArray()
                },
            SemanticCodeContext = MapSemanticCodeContext(run.Artifacts.SemanticCodeContext),
            ReviewDiscussionMessages = run.Artifacts.ReviewDiscussionMessages.Select(message => new ReviewCommentMessageDto
            {
                Role = message.Role,
                Content = message.Content,
                CreatedAt = message.CreatedAt,
                StructuredContent = MapStructuredContent(message.StructuredContent)
            }).ToArray(),
            InlineComments = run.Artifacts.InlineComments.Select(comment => new InlineCommentDto
            {
                Id = comment.Id,
                FindingId = comment.FindingId,
                FilePath = comment.FilePath,
                LineNumber = comment.LineNumber,
                Title = comment.Title,
                Severity = comment.Severity,
                Source = comment.Source,
                Category = comment.Category,
                Content = comment.Content,
                ExistingCode = comment.ExistingCode,
                Suggestion = comment.Suggestion,
                StartLine = comment.StartLine,
                EndLine = comment.EndLine,
                ContextBlock = comment.ContextBlock,
                ContextStartLine = comment.ContextStartLine,
                ContextEndLine = comment.ContextEndLine,
                RelevantDiffHunk = comment.RelevantDiffHunk,
                IsRelevant = comment.IsRelevant,
                PublishedToTfs = comment.PublishedToTfs,
                PublishedAt = comment.PublishedAt,
                Messages = comment.Messages.Select(message => new ReviewCommentMessageDto
                {
                    Role = message.Role,
                    Content = message.Content,
                    CreatedAt = message.CreatedAt,
                    StructuredContent = MapStructuredContent(message.StructuredContent)
                }).ToArray()
            }).ToArray(),
            ReviewedFiles = run.Artifacts.ReviewedFiles.Select(file => new ReviewedFileDto
            {
                FilePath = file.FilePath,
                DisplayName = file.DisplayName,
                ChangeType = file.ChangeType,
                AddedLines = file.AddedLines,
                DeletedLines = file.DeletedLines,
                DiffPatch = file.DiffPatch,
                FullContent = file.FullContent,
                ChangedLineNumbers = file.ChangedLineNumbers,
                InlineThreads = file.InlineThreads.Select(comment => new InlineCommentDto
                {
                    Id = comment.Id,
                    FindingId = comment.FindingId,
                    FilePath = comment.FilePath,
                    LineNumber = comment.LineNumber,
                    Title = comment.Title,
                    Severity = comment.Severity,
                    Source = comment.Source,
                    Category = comment.Category,
                    Content = comment.Content,
                    ExistingCode = comment.ExistingCode,
                    Suggestion = comment.Suggestion,
                    StartLine = comment.StartLine,
                    EndLine = comment.EndLine,
                    ContextBlock = comment.ContextBlock,
                    ContextStartLine = comment.ContextStartLine,
                    ContextEndLine = comment.ContextEndLine,
                    RelevantDiffHunk = comment.RelevantDiffHunk,
                    IsRelevant = comment.IsRelevant,
                    PublishedToTfs = comment.PublishedToTfs,
                    PublishedAt = comment.PublishedAt,
                    Messages = comment.Messages.Select(message => new ReviewCommentMessageDto
                    {
                        Role = message.Role,
                        Content = message.Content,
                        CreatedAt = message.CreatedAt,
                        StructuredContent = MapStructuredContent(message.StructuredContent)
                    }).ToArray()
                }).ToArray()
            }).ToArray(),
            ProgressUpdates = run.Artifacts.ProgressUpdates.Select(update => new ReviewProgressUpdateDto
            {
                RunId = update.RunId,
                Status = update.Status,
                Stage = update.Stage,
                ProgressPercent = update.ProgressPercent,
                Message = update.Message,
                Timestamp = update.Timestamp,
                IsTerminal = update.IsTerminal
            }).ToArray()
        };
    }

    private static SemanticCodeContextDto MapSemanticCodeContext(SemanticCodeContextArtifact artifact)
    {
        return new SemanticCodeContextDto
        {
            Enabled = artifact.Enabled,
            Attempted = artifact.Attempted,
            Succeeded = artifact.Succeeded,
            TimedOut = artifact.TimedOut,
            CacheReuseEnabled = artifact.CacheReuseEnabled,
            SourceCacheHit = artifact.SourceCacheHit,
            TargetCacheHit = artifact.TargetCacheHit,
            SourceFilesSelected = artifact.SourceFilesSelected,
            TargetFilesSelected = artifact.TargetFilesSelected,
            SourceFilesIndexed = artifact.SourceFilesIndexed,
            TargetFilesIndexed = artifact.TargetFilesIndexed,
            SourceChunksIndexed = artifact.SourceChunksIndexed,
            TargetChunksIndexed = artifact.TargetChunksIndexed,
            SourceFilesMissing = artifact.SourceFilesMissing,
            TargetFilesMissing = artifact.TargetFilesMissing,
            SourceFilesTooLarge = artifact.SourceFilesTooLarge,
            TargetFilesTooLarge = artifact.TargetFilesTooLarge,
            SourceFilesEmpty = artifact.SourceFilesEmpty,
            TargetFilesEmpty = artifact.TargetFilesEmpty,
            SourceFilesWithoutChunks = artifact.SourceFilesWithoutChunks,
            TargetFilesWithoutChunks = artifact.TargetFilesWithoutChunks,
            SourceFilesReadFailed = artifact.SourceFilesReadFailed,
            TargetFilesReadFailed = artifact.TargetFilesReadFailed,
            SourceFilesSkippedDeleted = artifact.SourceFilesSkippedDeleted,
            TargetFilesSkippedAdded = artifact.TargetFilesSkippedAdded,
            TargetBaselineFilesSelected = artifact.TargetBaselineFilesSelected,
            QueryCount = artifact.QueryCount,
            CandidateCount = artifact.CandidateCount,
            SnippetCount = artifact.SnippetCount,
            ElapsedMilliseconds = artifact.ElapsedMilliseconds,
            Status = artifact.Status,
            Message = artifact.Message,
            SourceCommitSha = artifact.SourceCommitSha,
            TargetCommitSha = artifact.TargetCommitSha,
            Snippets = artifact.Snippets.Select(snippet => new SemanticCodeContextSnippetDto
            {
                RevisionKind = snippet.RevisionKind,
                CommitSha = snippet.CommitSha,
                FilePath = snippet.FilePath,
                StartLine = snippet.StartLine,
                EndLine = snippet.EndLine,
                Score = snippet.Score,
                Query = snippet.Query
            }).ToArray()
        };
    }

    private static ReviewHistoryItemDto ToHistoryItemDto(ReviewRun run)
    {
        var criticalCount = run.Findings.Count(finding =>
            finding.Severity == FindingSeverity.Critical);
        var highCount = run.Findings.Count(finding =>
            finding.Severity == FindingSeverity.High);

        return new ReviewHistoryItemDto
        {
            Id = run.Id,
            Status = run.Status,
            Title = run.DisplayTitle,
            ServiceName = run.ServiceName,
            AuthorName = run.AuthorName,
            ProviderProfileId = run.ProviderProfileId,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            FindingsCount = run.Findings.Count,
            CriticalCount = criticalCount,
            HighCount = highCount,
            PublishSucceeded = run.PublishSucceeded,
            HasMarkdownReportArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport)
        };
    }

    private static InlineDiscussionStructuredContentDto? MapStructuredContent(InlineDiscussionStructuredContent? content)
    {
        if (content is null)
        {
            return null;
        }

        return new InlineDiscussionStructuredContentDto
        {
            Summary = content.Summary,
            Problems = content.Problems,
            Risk = content.Risk,
            Recommendations = content.Recommendations,
            ShouldPublishToTfs = content.ShouldPublishToTfs,
            PublishToTfsReason = content.PublishToTfsReason,
            ExampleCodeLanguage = content.ExampleCodeLanguage,
            ExampleCode = content.ExampleCode,
            AddedFindingsCount = content.AddedFindingsCount,
            AddedFindings = content.AddedFindings,
            AddedOpportunitiesCount = content.AddedOpportunitiesCount,
            AddedOpportunities = content.AddedOpportunities,
            AddedOpportunityItems = content.AddedOpportunityItems
                .Select(item => new InlineDiscussionOpportunityItemDto
                {
                    File = item.File,
                    LineHint = item.LineHint,
                    StartLine = item.StartLine,
                    Title = item.Title,
                    Description = item.Description
                })
                .ToArray()
        };
    }

    private static ChangeDescriptionStructuredContentDto? MapChangeDescription(ChangeDescriptionStructuredContent? content)
    {
        if (content is null)
        {
            return null;
        }

        return new ChangeDescriptionStructuredContentDto
        {
            Category = content.Category,
            Summary = content.Summary,
            ImpactedModules = content.ImpactedModules,
            Risks = content.Risks,
            EstimatedReviewEffort = content.EstimatedReviewEffort,
            QualityScore = content.QualityScore
        };
    }

    private static ReviewFindingDto MapFinding(ReviewFinding finding)
    {
        return new ReviewFindingDto
        {
            File = finding.File,
            LineHint = finding.LineHint,
            StartLine = finding.StartLine,
            EndLine = finding.EndLine,
            Category = finding.Category,
            Severity = finding.Severity,
            Source = finding.Source,
            Title = finding.Title,
            Description = finding.Description,
            ExistingCode = finding.ExistingCode,
            Suggestion = finding.Suggestion
        };
    }

    private static ReviewOpportunityItemDto MapOpportunity(ReviewOpportunityItem opportunity)
    {
        return new ReviewOpportunityItemDto
        {
            File = opportunity.File,
            LineHint = opportunity.LineHint,
            StartLine = opportunity.StartLine,
            Title = opportunity.Title,
            Description = opportunity.Description,
            Suggestion = opportunity.Suggestion
        };
    }
}
