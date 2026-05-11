using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class ExternalReviewArtifactParserTests
{
    [Fact]
    public void Parse_Extracts_PrAgent_Description_Diagram_And_Findings()
    {
        var artifact = new ExternalReviewArtifact
        {
            Attempted = true,
            Succeeded = true,
            EngineName = "PR-Agent",
            Commands =
            [
                new ExternalReviewCommandArtifact
                {
                    Command = "describe",
                    Succeeded = true,
                    Artifact = """
                        ### **PR Type**
                        Enhancement, Tests

                        ___

                        ### **Description**
                        - Добавлен кэш регионов
                        - Удалены SQL-джойны с ReplicBranch

                        ___

                        ### Diagram Walkthrough

                        ```mermaid
                        flowchart LR
                          A["Provider"] --> B["RegionCacheRepository"]
                        ```

                        <details> <summary><h3> File Walkthrough</h3></summary>ignored</details>
                        """
                },
                new ExternalReviewCommandArtifact
                {
                    Command = "review",
                    Succeeded = true,
                    Artifact = """
                        ## PR Reviewer Guide

                        <details><summary><a href='https://gitlab.example/project/-/blob/feature%2Ftest/src/GetOrderList.sql?ref_type=heads#L64-65'><strong>Изменение логики фильтрации</strong></a>

                        В SQL-запросе удалены условия `rb."IsBasic"` и `rb."IsBcAllowed"`, которые ранее фильтровали филиалы.
                        </summary>

                        ```sql
                        where (@orderId is null or o."OrderId" = @orderId)
                        ```

                        </details>
                        """
                }
            ]
        };

        var sut = new ExternalReviewArtifactParser();

        var result = sut.Parse(artifact);

        Assert.NotNull(result.ChangeSummary);
        Assert.Contains("Добавлен кэш регионов", result.ChangeSummary!.Description);
        Assert.StartsWith("flowchart LR", result.ChangeSummary.DiagramMermaid);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/GetOrderList.sql", finding.File);
        Assert.Equal("L64-L65", finding.LineHint);
        Assert.Equal(64, finding.StartLine);
        Assert.Equal(65, finding.EndLine);
        Assert.Equal(ReviewFindingSource.ExternalReview, finding.Source);
        Assert.Equal(FindingCategory.Logic, finding.Category);
        Assert.Contains("IsBasic", finding.Description);
        Assert.Contains("where", finding.ExistingCode);
    }

    [Fact]
    public void DisplayFormatter_Converts_PrAgent_Html_To_Readable_Markdown()
    {
        var command = new ExternalReviewCommandArtifact
        {
            Command = "review",
            Succeeded = true,
            Artifact = """
                ## PR Reviewer Guide

                <table><tr><td>⏱️&nbsp;<strong>Estimated effort to review</strong>: 3 🔵🔵🔵⚪⚪</td></tr></table>

                <details><summary><a href='https://gitlab.example/project/-/blob/feature%2Ftest/src/GetOrderList.sql?ref_type=heads#L64-65'><strong>Изменение логики фильтрации</strong></a>

                В SQL-запросе удалены условия `rb."IsBasic"` и `rb."IsBcAllowed"`.
                </summary>

                ```sql
                where (@orderId is null or o."OrderId" = @orderId)
                ```

                </details>
                """
        };

        var result = ExternalReviewArtifactDisplayFormatter.Format(command);

        Assert.Contains("### PR Reviewer Guide", result);
        Assert.Contains("#### Изменение логики фильтрации", result);
        Assert.Contains("`src/GetOrderList.sql:L64-L65`", result);
        Assert.Contains("```sql", result);
        Assert.DoesNotContain("<table", result);
        Assert.DoesNotContain("<details", result);
        Assert.DoesNotContain("&nbsp;", result);
    }

    [Fact]
    public void DisplayFormatter_Removes_PrAgent_File_Walkthrough_From_Describe()
    {
        var command = new ExternalReviewCommandArtifact
        {
            Command = "describe",
            Succeeded = true,
            Artifact = """
                ### **PR Type**
                Enhancement

                ___

                ### **Description**
                - Реализовано кэширование данных регионов

                ### Diagram Walkthrough
                ```mermaid
                flowchart LR
                  A --> B
                ```

                <details><summary><h3> File Walkthrough</h3></summary><table><tr><td>raw</td></tr></table></details>
                """
        };

        var result = ExternalReviewArtifactDisplayFormatter.Format(command);

        Assert.Contains("### PR Type", result);
        Assert.Contains("Реализовано кэширование данных регионов", result);
        Assert.Contains("Диаграмма применена", result);
        Assert.DoesNotContain("File Walkthrough", result);
        Assert.DoesNotContain("<details", result);
        Assert.DoesNotContain("flowchart LR", result);
    }
}
