using System.Text.Json;
using System.Text.Json.Serialization;

namespace TfsReviewPlatform.Application.Models.Graph;

public sealed class CodeGraph
{
    public CodeGraph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyList<GraphNode> Nodes { get; }

    public IReadOnlyList<GraphEdge> Edges { get; }

    private Dictionary<string, GraphNode>? _byId;

    public GraphNode? TryGetNode(string id)
    {
        _byId ??= Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        return _byId.TryGetValue(id, out var node) ? node : null;
    }

    public IEnumerable<GraphEdge> Outgoing(string fromId) =>
        Edges.Where(e => string.Equals(e.From, fromId, StringComparison.Ordinal));

    public IEnumerable<GraphEdge> Incoming(string toId) =>
        Edges.Where(e => string.Equals(e.To, toId, StringComparison.Ordinal));

    /// <summary>
    /// Serializes the graph back to JSON (same shape as RoslynGraphBuilder output: <c>nodes</c> + <c>edges</c>).
    /// </summary>
    public string ToJsonString()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        return JsonSerializer.Serialize(new { nodes = Nodes, edges = Edges }, options);
    }

    public static CodeGraph? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var nodes = root.GetProperty("nodes").EnumerateArray().Select(ParseNode).ToArray();
            var edges = root.GetProperty("edges").EnumerateArray().Select(ParseEdge).ToArray();
            return new CodeGraph(nodes, edges);
        }
        catch
        {
            return null;
        }
    }

    private static GraphNode ParseNode(JsonElement e)
    {
        var id = ReadString(e, "id") ?? string.Empty;
        var kind = ReadString(e, "kind") ?? string.Empty;
        var name = ReadString(e, "name") ?? string.Empty;
        var filePath = ReadString(e, "filePath");
        int? line = null;
        if (e.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number && lineEl.TryGetInt32(out var ln))
        {
            line = ln;
        }

        var assemblyName = ReadString(e, "assemblyName");
        return new GraphNode
        {
            Id = id,
            Kind = kind,
            Name = name,
            FilePath = filePath,
            Line = line,
            AssemblyName = assemblyName
        };
    }

    private static GraphEdge ParseEdge(JsonElement e) =>
        new()
        {
            From = ReadString(e, "from") ?? string.Empty,
            To = ReadString(e, "to") ?? string.Empty,
            Kind = ReadString(e, "kind") ?? string.Empty
        };

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
