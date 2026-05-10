using System.Text.Json;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

/// <summary>
/// Maps LLM chunk-review JSON to domain entities without deduplication, caps, or heuristic filtering.
/// </summary>
public interface IChunkReviewResponseParser
{
    (IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<ReviewOpportunityItem> Opportunities) ParseChunkResponse(string raw);

    IReadOnlyList<ReviewFinding> ParseFindingsJsonArray(JsonElement arrayElement);
}
