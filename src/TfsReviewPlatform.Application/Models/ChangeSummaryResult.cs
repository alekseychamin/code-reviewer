using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed class ChangeSummaryResult
{
    public string Description { get; init; } = string.Empty;

    public string? DiagramMermaid { get; init; }

    public ChangeDescriptionStructuredContent? StructuredContent { get; init; }
}
