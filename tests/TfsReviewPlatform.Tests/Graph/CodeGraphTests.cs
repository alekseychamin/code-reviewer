using TfsReviewPlatform.Application.Models.Graph;

namespace TfsReviewPlatform.Tests.Graph;

public sealed class CodeGraphTests
{
    [Fact]
    public void TryParse_Parses_Nodes_And_Edges()
    {
        const string json = """
            {
              "nodes": [
                { "id": "sym:A", "kind": "Method", "name": "Foo.M", "filePath": "src/Foo.cs", "line": 10, "assemblyName": "Asm" }
              ],
              "edges": [
                { "from": "sym:A", "to": "sym:B", "kind": "CALLS" }
              ]
            }
            """;

        var graph = CodeGraph.TryParse(json);
        Assert.NotNull(graph);
        Assert.Single(graph!.Nodes);
        Assert.Single(graph.Edges);
        Assert.Equal("sym:A", graph.Nodes[0].Id);
        Assert.Equal("CALLS", graph.Edges[0].Kind);
        Assert.NotNull(graph.TryGetNode("sym:A"));
    }

    [Fact]
    public void ToJsonString_RoundTrips_Through_TryParse()
    {
        const string json = """
            {
              "nodes": [
                { "id": "sym:A", "kind": "Method", "name": "Foo.M", "filePath": "src/Foo.cs", "line": 10, "assemblyName": "Asm" }
              ],
              "edges": [
                { "from": "sym:A", "to": "sym:B", "kind": "CALLS" }
              ]
            }
            """;

        var original = CodeGraph.TryParse(json);
        Assert.NotNull(original);
        var back = CodeGraph.TryParse(original!.ToJsonString());
        Assert.NotNull(back);
        Assert.Equal(original.Nodes.Count, back!.Nodes.Count);
        Assert.Equal(original.Edges.Count, back.Edges.Count);
        Assert.Equal("sym:A", back.Nodes[0].Id);
        Assert.Equal("CALLS", back.Edges[0].Kind);
    }
}
