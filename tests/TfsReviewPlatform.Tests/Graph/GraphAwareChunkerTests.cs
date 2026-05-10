using Microsoft.Extensions.Logging.Abstractions;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Models.Graph;
using TfsReviewPlatform.Integrations.Roslyn;

namespace TfsReviewPlatform.Tests.Graph;

public sealed class GraphAwareChunkerTests
{
    private sealed class StubSnippetLoader : IGraphChunkContextLoader
    {
        public Task<string?> TryLoadSnippetAsync(
            string workspaceRoot,
            string repoRelativeFilePath,
            int centerLine,
            int contextLines,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>($"snippet:{repoRelativeFilePath}:{centerLine}");
    }

    [Fact]
    public async Task Augment_Fallback_When_Graph_Null_Returns_Same_Chunks()
    {
        var pre = new PreprocessedDiff
        {
            FilteredDiffText = "x",
            ReviewContextDiffText = "x",
            ReviewChunks = ["chunk1"],
            Chunks = ["chunk1"]
        };

        var sut = new GraphAwareChunker(new StubSnippetLoader(), NullLogger<GraphAwareChunker>.Instance);
        var result = await sut.AugmentWithGraphChunksAsync(
            pre,
            null,
            "/tmp",
            new ReviewPipelineOptions(),
            CancellationToken.None);

        Assert.Same(pre.ReviewChunks, result.ReviewChunks);
    }

    [Fact]
    public async Task Augment_Builds_Chunk_When_Anchor_Matches()
    {
        var diff = """
            diff --git a/Sample.cs b/Sample.cs
            --- /dev/null
            +++ b/Sample.cs
            @@ -0,0 +1,1 @@
            +void F(){}
            """;

        var nodes = new List<GraphNode>
        {
            new()
            {
                Id = "sym:f",
                Kind = "Method",
                Name = "F()",
                FilePath = "Sample.cs",
                Line = 1
            },
            new()
            {
                Id = "sym:h",
                Kind = "Method",
                Name = "Helper",
                FilePath = "Other.cs",
                Line = 5
            }
        };
        var edges = new List<GraphEdge>
        {
            new() { From = "sym:f", To = "sym:h", Kind = "CALLS" }
        };
        var graph = new CodeGraph(nodes, edges);

        var pre = new PreprocessedDiff
        {
            FilteredDiffText = diff,
            ReviewContextDiffText = diff,
            ReviewChunks = ["old"],
            Chunks = ["old"]
        };

        var sut = new GraphAwareChunker(new StubSnippetLoader(), NullLogger<GraphAwareChunker>.Instance);
        var result = await sut.AugmentWithGraphChunksAsync(
            pre,
            graph,
            "/tmp",
            new ReviewPipelineOptions { MaxPrimaryReviewChunkCharacters = 20000 },
            CancellationToken.None);

        Assert.NotNull(result.Graph);
        Assert.Single(result.ReviewChunks);
        Assert.Contains("Changed code (review required)", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Contains("Related context", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Contains("snippet:Other.cs:5", result.ReviewChunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Augment_Includes_RefSite_Snippet_When_IncludeReferenceEdges_True()
    {
        var diff = """
            diff --git a/Sample.cs b/Sample.cs
            --- /dev/null
            +++ b/Sample.cs
            @@ -0,0 +1,1 @@
            +void M(){}
            """;

        var typeId = "sym:type:Cont";
        var methodId = "sym:method:M";
        var locId = "loc:Other.cs:7";
        var nodes = new List<GraphNode>
        {
            new() { Id = typeId, Kind = "NamedType", Name = "Cont", FilePath = "Sample.cs", Line = 1 },
            new() { Id = methodId, Kind = "Method", Name = "M()", FilePath = "Sample.cs", Line = 1 },
            new() { Id = locId, Kind = "Location", Name = "Other.cs:7", FilePath = "Other.cs", Line = 7 }
        };
        var edges = new List<GraphEdge>
        {
            new() { From = typeId, To = methodId, Kind = "CONTAINS" },
            new() { From = typeId, To = locId, Kind = "REFERENCED_BY" }
        };
        var graph = new CodeGraph(nodes, edges);

        var pre = new PreprocessedDiff
        {
            FilteredDiffText = diff,
            ReviewContextDiffText = diff,
            ReviewChunks = ["old"],
            Chunks = ["old"]
        };

        var sut = new GraphAwareChunker(new StubSnippetLoader(), NullLogger<GraphAwareChunker>.Instance);
        var opts = new ReviewPipelineOptions
        {
            MaxPrimaryReviewChunkCharacters = 20000,
            Roslyn = new RoslynGraphPipelineOptions { IncludeReferenceEdges = true }
        };

        var result = await sut.AugmentWithGraphChunksAsync(pre, graph, "/tmp", opts, CancellationToken.None);

        Assert.Single(result.ReviewChunks);
        Assert.Contains("RefSite", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Contains("snippet:Other.cs:7", result.ReviewChunks[0], StringComparison.Ordinal);
    }
}
