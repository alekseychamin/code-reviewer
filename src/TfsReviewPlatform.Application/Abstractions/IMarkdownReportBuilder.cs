using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IMarkdownReportBuilder
{
    string BuildFullReport(string reviewTitle, string description, IReadOnlyList<ReviewFinding> findings);

    string BuildSummaryComment(string reviewTitle, string description, IReadOnlyList<ReviewFinding> findings);

    IReadOnlyList<InlineCommentDraft> BuildInlineComments(
        IReadOnlyList<ReviewFinding> findings,
        string diffText);
}
