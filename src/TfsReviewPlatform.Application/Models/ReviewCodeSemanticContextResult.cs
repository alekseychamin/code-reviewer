using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed record ReviewCodeSemanticContextResult(
    IReadOnlyList<ReviewWorkspaceToolResponse> Snippets,
    SemanticCodeContextArtifact Diagnostics)
{
    public static ReviewCodeSemanticContextResult Empty(SemanticCodeContextArtifact diagnostics)
        => new([], diagnostics);
}
