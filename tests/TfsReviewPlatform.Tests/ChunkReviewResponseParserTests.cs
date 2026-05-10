using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class ChunkReviewResponseParserTests
{
    [Fact]
    public void ParseChunkResponse_Maps_Findings_Without_Filtering()
    {
        var raw = """
            {
              "findings": [
                {
                  "kind": "Defect",
                  "file": "a.cs",
                  "line_hint": "M",
                  "type": "Bug",
                  "severity": "High",
                  "title": "T",
                  "description": "D",
                  "existing_code": "x",
                  "suggestion": "y"
                }
              ],
              "opportunities": [],
              "need_more_context": false,
              "tool_requests": []
            }
            """;

        var sut = new ChunkReviewResponseParser();
        var (findings, opps) = sut.ParseChunkResponse(raw);

        Assert.Single(findings);
        Assert.Empty(opps);
        Assert.Equal("a.cs", findings[0].File);
        Assert.Equal(ReviewFindingSource.InitialReview, findings[0].Source);
    }
}
