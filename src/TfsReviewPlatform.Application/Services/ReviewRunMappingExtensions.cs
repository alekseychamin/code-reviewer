using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Services;

public static class ReviewRunMappingExtensions
{
    public static ReviewRunDto ToDto(this ReviewRun run)
    {
        return new ReviewRunDto
        {
            Id = run.Id,
            Status = run.Status,
            TargetKind = run.Target.Kind,
            Title = run.Target.Title,
            ProviderProfileId = run.ProviderProfileId,
            CurrentStage = run.CurrentStage,
            ProgressPercent = run.ProgressPercent,
            CurrentMessage = run.CurrentMessage,
            ErrorMessage = run.ErrorMessage,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            ChangeDescription = run.Artifacts.ChangeDescription,
            ChangeDescriptionStructured = MapChangeDescription(run.Artifacts.ChangeDescriptionStructured),
            ChangeDiagramMermaid = run.Artifacts.ChangeDiagramMermaid,
            HasDiffArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.DiffText),
            MarkdownReport = run.Artifacts.MarkdownReport,
            HasMarkdownReportArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport),
            SummaryComment = run.Artifacts.SummaryComment,
            PublishSucceeded = run.PublishSucceeded,
            ChangedFiles = run.Artifacts.ChangedFiles,
            Findings = run.Findings.Select(MapFinding).ToArray(),
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
                    NewFindings = run.Artifacts.FindingsComparison.NewFindings.Select(MapFinding).ToArray(),
                    StillRelevantFindings = run.Artifacts.FindingsComparison.StillRelevantFindings.Select(MapFinding).ToArray(),
                    ResolvedFindings = run.Artifacts.FindingsComparison.ResolvedFindings.Select(MapFinding).ToArray()
                },
            InlineComments = run.Artifacts.InlineComments.Select(comment => new InlineCommentDto
            {
                Id = comment.Id,
                FilePath = comment.FilePath,
                LineNumber = comment.LineNumber,
                Title = comment.Title,
                Severity = comment.Severity,
                Content = comment.Content,
                ExistingCode = comment.ExistingCode,
                Suggestion = comment.Suggestion,
                ContextBlock = comment.ContextBlock,
                ContextStartLine = comment.ContextStartLine,
                ContextEndLine = comment.ContextEndLine,
                RelevantDiffHunk = comment.RelevantDiffHunk,
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
                    FilePath = comment.FilePath,
                    LineNumber = comment.LineNumber,
                    Title = comment.Title,
                    Severity = comment.Severity,
                    Content = comment.Content,
                    ExistingCode = comment.ExistingCode,
                    Suggestion = comment.Suggestion,
                    ContextBlock = comment.ContextBlock,
                    ContextStartLine = comment.ContextStartLine,
                    ContextEndLine = comment.ContextEndLine,
                    RelevantDiffHunk = comment.RelevantDiffHunk,
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
            }).ToArray()
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
            ExampleCode = content.ExampleCode
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
            Risks = content.Risks
        };
    }

    private static ReviewFindingDto MapFinding(ReviewFinding finding)
    {
        return new ReviewFindingDto
        {
            File = finding.File,
            LineHint = finding.LineHint,
            Category = finding.Category,
            Severity = finding.Severity,
            Title = finding.Title,
            Description = finding.Description,
            ExistingCode = finding.ExistingCode,
            Suggestion = finding.Suggestion
        };
    }
}
