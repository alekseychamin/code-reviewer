using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class MarkdownReportBuilderTests
{
    [Fact]
    public void BuildInlineComments_MapsFindingToDiffLine()
    {
        var sut = new MarkdownReportBuilder();
        var findings = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "ExecuteAsync",
                FindingCategory.Bug,
                FindingSeverity.Critical,
                "Dangerous null path",
                "The new code dereferences a value before checking null.",
                "return request.Value.Length;",
                "return request?.Value?.Length ?? 0;")
        };

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            @@ -10,1 +10,2 @@
            +return request.Value.Length;
            """;

        var comments = sut.BuildInlineComments(findings, diff);

        Assert.Single(comments);
        Assert.Equal("/src/App/Service.cs", comments[0].FilePath);
        Assert.Equal(10, comments[0].LineNumber);
    }
}
