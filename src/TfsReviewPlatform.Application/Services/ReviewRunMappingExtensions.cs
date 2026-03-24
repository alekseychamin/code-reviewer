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
            MarkdownReport = run.Artifacts.MarkdownReport,
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
                FilePath = comment.FilePath,
                LineNumber = comment.LineNumber,
                Content = comment.Content
            }).ToArray()
        };
    }
}
