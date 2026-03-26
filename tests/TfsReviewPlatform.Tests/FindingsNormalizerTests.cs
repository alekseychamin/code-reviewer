using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class FindingsNormalizerTests
{
    [Fact]
    public void Normalize_ParsesFencedJson_AndDeduplicatesByFileAndTitle()
    {
        var sut = new FindingsNormalizer();
        var responses = new[]
        {
            """
            ```json
            [
              {
                "file": "src/Service.cs",
                "line_hint": "ExecuteAsync",
                "type": "Bug",
                "severity": "High",
                "title": "Blocking async call",
                "description": "Task.Result can deadlock.",
                "existing_code": "var x = task.Result;",
                "suggestion": "var x = await task;"
              }
            ]
            ```
            """,
            """
            [
              {
                "file": "src/Service.cs",
                "line_hint": "ExecuteAsync",
                "type": "Bug",
                "severity": "High",
                "title": "Blocking async call",
                "description": "A longer duplicate description that should win.",
                "existing_code": "var x = task.Result;",
                "suggestion": "var x = await task;"
              }
            ]
            """
        };

        var findings = sut.Normalize(responses);

        Assert.Single(findings);
        Assert.Equal(FindingSeverity.High, findings[0].Severity);
        Assert.Contains("longer duplicate", findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_SkipsImprovementOnly_AndCodeStyleFindings()
    {
        var sut = new FindingsNormalizer();
        var responses = new[]
        {
            """
            [
              {
                "kind": "Improvement",
                "file": "src/Service.cs",
                "line_hint": "ExecuteAsync",
                "type": "Performance",
                "severity": "Low",
                "title": "Use better naming",
                "description": "Could be renamed for readability.",
                "existing_code": "var x = task.Result;",
                "suggestion": "Rename the variable."
              },
              {
                "kind": "Defect",
                "file": "src/Style.cs",
                "line_hint": "MapAsync",
                "type": "CodeStyle",
                "severity": "Medium",
                "title": "Prefer explicit type",
                "description": "Use explicit types for clarity.",
                "existing_code": "var result = Map();",
                "suggestion": "Use the concrete type."
              },
              {
                "kind": "Risk",
                "file": "src/Service.cs",
                "line_hint": "ExecuteAsync",
                "type": "Bug",
                "severity": "High",
                "title": "Blocking async call",
                "description": "Task.Result can deadlock.",
                "existing_code": "var x = task.Result;",
                "suggestion": "var x = await task;"
              }
            ]
            """
        };

        var findings = sut.Normalize(responses);

        Assert.Single(findings);
        Assert.Equal("Blocking async call", findings[0].Title);
        Assert.Equal(FindingCategory.Bug, findings[0].Category);
        Assert.Equal(FindingSeverity.High, findings[0].Severity);
    }
}
