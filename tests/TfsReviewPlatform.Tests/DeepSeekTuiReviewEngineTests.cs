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
