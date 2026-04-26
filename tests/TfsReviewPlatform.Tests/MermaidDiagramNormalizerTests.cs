using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Tests;

public sealed class MermaidDiagramNormalizerTests
{
    [Fact]
    public void Normalize_StripsMarkdownFenceAndLeadingText()
    {
        var normalized = MermaidDiagramNormalizer.Normalize("""
            Here is the diagram:

            ```mermaid
            flowchart LR
              A[API] --> B[Service]
            ```
            """);

        Assert.Equal("""
            flowchart LR
              A["API"] --> B["Service"]
            """, normalized);
    }

    [Fact]
    public void Normalize_ConvertsColonEdgeLabelsToMermaidPipes()
    {
        var normalized = MermaidDiagramNormalizer.Normalize("""
            flowchart LR
                Client --> BCOP: requests orders
                BCOP -.-> CacheOpt: reads options
            """);

        Assert.Equal("""
            flowchart LR
                Client -->|requests orders| BCOP
                BCOP -.->|reads options| CacheOpt
            """, normalized);
    }

    [Fact]
    public void Normalize_DoesNotDoubleQuoteAlreadyQuotedNodeLabels()
    {
        var normalized = MermaidDiagramNormalizer.Normalize("""
            flowchart LR
                Client["Order Export Consumer"]
            """);

        Assert.Equal("""
            flowchart LR
                Client["Order Export Consumer"]
            """, normalized);
    }

    [Fact]
    public void Normalize_ReplacesJsonStyleNewlinesInLabelsWithBr()
    {
        var diagram = """
            flowchart LR
              DB["Database
            """ + "\\n" + """
            (ReplicBranch)"]
            """;

        var normalized = MermaidDiagramNormalizer.Normalize(diagram);

        Assert.Contains("Database<br/>(ReplicBranch)", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_FixesBogusParenQuoteWrapperInNodeLabel()
    {
        var normalized = MermaidDiagramNormalizer.Normalize("""
            flowchart LR
                Cache["('RegionCacheRepository<br/>(In-Memory Cache)'"]
            """);

        Assert.Contains("RegionCacheRepository", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("('", normalized, StringComparison.Ordinal);
        Assert.Contains("Cache[\"RegionCacheRepository", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_ConvertsDoubleDashQuotedTextEdges()
    {
        var normalized = MermaidDiagramNormalizer.Normalize("""
            flowchart LR
                DB -- "fetches regions" --> BG
            """);

        Assert.Contains("DB -->|fetches regions| BG", normalized, StringComparison.Ordinal);
    }
}
