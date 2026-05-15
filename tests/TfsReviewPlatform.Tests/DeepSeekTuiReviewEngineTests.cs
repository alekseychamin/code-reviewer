using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Integrations.ExternalReview;

namespace TfsReviewPlatform.Tests;

public sealed class DeepSeekTuiReviewEngineTests
{
    [Fact]
    public void ParseReviewPayload_ExtractsFinalJsonFromStreamJsonOutput()
    {
        var raw = """
            {"type":"assistant_delta","delta":{"content":"thinking..."}}
            {"type":"assistant_delta","delta":{"content":"{\"findings\":[{\"file\":\"src/Auth/TokenService.cs\",\"line_hint\":\"RefreshAsync\",\"start_line\":42,\"end_line\":45,\"category\":\"Security\",\"severity\":\"High\",\"title\":\"Refresh identity-токена сравнивает UTC JWT с локальным временем\",\"description\":\"Сравнение exp из JWT в UTC с DateTime.Now зависит от локальной зоны и может преждевременно инвалидировать токен.\",\"existing_code\":\"DateTime.Now\",\"suggestion\":\"Сравнивать с DateTimeOffset.UtcNow или Clock.UtcNow.\"}],\"opportunities\":[{\"file\":\"src/Auth/TokenService.cs\",\"line_hint\":\"RefreshAsync\",\"title\":\"Добавить тест на границу истечения\",\"description\":\"Нет проверки на UTC/local mismatch.\",\"suggestion\":\"Добавить unit-тест с локальной зоной, отличной от UTC.\"}]}"}}
            """;

        var parsed = DeepSeekTuiReviewEngine.ParseReviewPayload(raw);

        Assert.NotNull(parsed);
        var finding = Assert.Single(parsed.Findings);
        Assert.Equal("src/Auth/TokenService.cs", finding.File);
        Assert.Equal(FindingCategory.Security, finding.Category);
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("Refresh identity-токена сравнивает UTC JWT с локальным временем", finding.Title);
        Assert.Equal(42, finding.StartLine);
        Assert.Single(parsed.Opportunities);
    }

    [Fact]
    public void ParseReviewPayload_ExtractsFencedJson()
    {
        var raw = """
            Here is the result:
            ```json
            {
              "findings": [],
              "opportunities": []
            }
            ```
            """;

        var parsed = DeepSeekTuiReviewEngine.ParseReviewPayload(raw);

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Findings);
        Assert.Empty(parsed.Opportunities);
    }

    [Fact]
    public void ParseReviewPayload_IgnoresReviewTraceAuditFields()
    {
        var raw = """
            ```json
            {
              "summary": "demo",
              "review_trace": {
                "active_lenses": [],
                "hypotheses": [],
                "omitted_concerns": [
                  {
                    "concern": "Duplicated enrichment overloads",
                    "reason": "reported as opportunity"
                  }
                ]
              },
              "findings": [],
              "opportunities": [
                {
                  "file": "src/RegionCacheExtensions.cs",
                  "line_hint": "EnrichWithRegionData overloads",
                  "title": "Свести повторяющуюся логику enrichment",
                  "description": "Несколько overload-ов повторяют lookup региона и могут разойтись при следующем изменении.",
                  "suggestion": "Вынести общий lookup в общий helper."
                }
              ]
            }
            ```
            """;

        var parsed = DeepSeekTuiReviewEngine.ParseReviewPayload(raw);

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Findings);
        var opportunity = Assert.Single(parsed.Opportunities);
        Assert.Equal("Свести повторяющуюся логику enrichment", opportunity.Title);
    }

    [Fact]
    public void ParseReviewPayloadArtifact_ReadsReviewResultJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deepseek-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "review-result.json"),
                """
                {
                  "summary": "demo",
                  "findings": [
                    {
                      "file": "src/Orders/OrderRepository.cs",
                      "line_hint": "GroupBy().First()",
                      "start_line": 12,
                      "end_line": 15,
                      "category": "Bug",
                      "severity": "Medium",
                      "title": "Недетерминированный выбор записи",
                      "description": "GroupBy().First() выбирает одну из дублей без устойчивого порядка.",
                      "existing_code": "GroupBy(...).First()",
                      "suggestion": "Добавить явный OrderBy или уникальный фильтр."
                    }
                  ],
                  "opportunities": []
                }
                """);

            var parsed = DeepSeekTuiReviewEngine.ParseReviewPayloadArtifact(directory);

            Assert.NotNull(parsed);
            var finding = Assert.Single(parsed.Findings);
            Assert.Equal("Недетерминированный выбор записи", finding.Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DescribeProgressLine_RecognizesToolCall()
    {
        var description = DeepSeekTuiReviewEngine.DescribeProgressLine(
            """{"type":"tool_call","name":"read_file","arguments":{"path":"src/Service.cs"}}""");

        Assert.Equal("использует инструмент read_file", description);
    }

    [Fact]
    public void DescribeProgressLine_RecognizesFinalJson()
    {
        var description = DeepSeekTuiReviewEngine.DescribeProgressLine(
            """{"type":"assistant_delta","delta":{"content":"{\"findings\":[],\"opportunities\":[]}"}}""");

        Assert.Equal("формирует структурированный JSON ревью", description);
    }
}
