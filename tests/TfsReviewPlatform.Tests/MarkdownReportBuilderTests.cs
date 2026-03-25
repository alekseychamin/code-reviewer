using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class MarkdownReportBuilderTests
{
    [Fact]
    public void BuildFullReport_IncludesComparisonSection()
    {
        var sut = new MarkdownReportBuilder();
        var findings = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "Line 10",
                FindingCategory.Bug,
                FindingSeverity.High,
                "Current issue",
                "Current issue description.",
                "return request.Value.Length;",
                "return request?.Value?.Length ?? 0;")
        };
        var comparison = new FindingsComparisonSnapshot
        {
            PreviousRunId = Guid.NewGuid(),
            PreviousFindingsCount = 2,
            CurrentFindingsCount = 1,
            NewFindingsCount = 1,
            StillRelevantFindingsCount = 0,
            ResolvedFindingsCount = 2,
            NewFindings = findings,
            ResolvedFindings =
            [
                new ReviewFinding(
                    "src/App/OldService.cs",
                    "Line 42",
                    FindingCategory.Bug,
                    FindingSeverity.Medium,
                    "Resolved issue",
                    "Resolved description.",
                    "var oldCode = value.Length;",
                    "var oldCode = value?.Length ?? 0;")
            ]
        };

        var report = sut.BuildFullReport("demo", "desc", findings, comparison);

        Assert.Contains("## Delta Since Previous Review", report);
        Assert.Contains("- Resolved: 2", report);
        Assert.Contains("Resolved issue", report);
    }

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

    [Fact]
    public void BuildInlineComments_PrefersAbsoluteLineHintWhenPresent()
    {
        var sut = new MarkdownReportBuilder();
        var findings = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "Line 42",
                FindingCategory.Bug,
                FindingSeverity.High,
                "Hidden null dereference",
                "Potential null dereference remains on the changed line.",
                "return request.Value.Length;",
                "return request?.Value?.Length ?? 0;")
        };

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            @@ -40,2 +40,4 @@
             var request = Load();
            +return request.Value.Length;
            +return request.Value.Length + 1;
            """;

        var comments = sut.BuildInlineComments(findings, diff);

        Assert.Single(comments);
        Assert.Equal(42, comments[0].LineNumber);
    }

    [Fact]
    public void BuildInlineComments_UsesFuzzySnippetMatchingWhenWhitespaceDiffers()
    {
        var sut = new MarkdownReportBuilder();
        var findings = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "ExecuteAsync",
                FindingCategory.CodeStyle,
                FindingSeverity.Medium,
                "Whitespace-insensitive match",
                "The reviewer should still land on the right added line.",
                "return request?.Value?.Length ?? 0;",
                "return request?.Value?.Length ?? defaultValue;")
        };

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            @@ -10,1 +10,2 @@
            +return request ?. Value ?. Length ?? 0;
            """;

        var comments = sut.BuildInlineComments(findings, diff);

        Assert.Single(comments);
        Assert.Equal(10, comments[0].LineNumber);
    }

    [Fact]
    public void BuildInlineComments_IgnoresUnverifiedLineHintAndFallsBackToSnippet()
    {
        var sut = new MarkdownReportBuilder();
        var findings = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "Line 103",
                FindingCategory.Bug,
                FindingSeverity.High,
                "Missing null check",
                "Null validation is missing before dereference.",
                "if (string.IsNullOrEmpty(cacheEntry.RequestId))",
                "Validate request id before use.")
        };

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            @@ -260,3 +265,6 @@
            +if (string.IsNullOrEmpty(cacheEntry.RequestId))
            +{
            +    throw new ArgumentNullException(nameof(cacheEntry.RequestId));
            +}
            """;

        var comments = sut.BuildInlineComments(findings, diff);

        Assert.Single(comments);
        Assert.Equal(265, comments[0].LineNumber);
    }
}
