using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed record ChunkReviewAgentEnvelope(
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewOpportunityItem> Opportunities,
    bool NeedMoreContext,
    IReadOnlyList<ReviewWorkspaceToolRequest> ToolRequests);
