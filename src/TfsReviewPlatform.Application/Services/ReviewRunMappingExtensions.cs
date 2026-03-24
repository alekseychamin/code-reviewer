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
            LocalOnlyMode = run.LocalOnlyMode,
            CurrentStage = run.CurrentStage,
            ProgressPercent = run.ProgressPercent,
            CurrentMessage = run.CurrentMessage,
            ErrorMessage = run.ErrorMessage,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            ChangeDescription = run.Artifacts.ChangeDescription,
            ChangeDiagramMermaid = run.Artifacts.ChangeDiagramMermaid,
            HasDiffArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.DiffText),
            MarkdownReport = run.Artifacts.MarkdownReport,
            HasMarkdownReportArtifact = !string.IsNullOrWhiteSpace(run.Artifacts.MarkdownReport),
            SummaryComment = run.Artifacts.SummaryComment,
            PublishSucceeded = run.PublishSucceeded,
            ChangedFiles = run.Artifacts.ChangedFiles,
            Findings = run.Findings.Select(finding => new ReviewFindingDto
            {
                File = finding.File,
                LineHint = finding.LineHint,
                Category = finding.Category,
                Severity = finding.Severity,
                Title = finding.Title,
                Description = finding.Description,
                ExistingCode = finding.ExistingCode,
                Suggestion = finding.Suggestion
            }).ToArray(),
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
                    CreatedAt = message.CreatedAt
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
                        CreatedAt = message.CreatedAt
                    }).ToArray()
                }).ToArray()
            }).ToArray()
        };
    }
}
