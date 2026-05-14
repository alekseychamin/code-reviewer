using System.Text;
using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Models.Graph;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Integrations.Roslyn;

public sealed class GraphAwareChunker(
    IGraphChunkContextLoader snippetLoader,
    ILogger<GraphAwareChunker> logger)
    : IGraphAwareChunker
{
    public async Task<PreprocessedDiff> AugmentWithGraphChunksAsync(
        PreprocessedDiff preprocessed,
        CodeGraph? graph,
        string workspaceRoot,
        ReviewPipelineOptions pipelineOptions,
        CancellationToken cancellationToken)
    {
        if (graph is null || graph.Nodes.Count == 0)
        {
            return preprocessed;
        }

        var maxPrimary = Math.Max(4000, pipelineOptions.MaxPrimaryReviewChunkCharacters);
        var maxPrepared = Math.Max(2000, pipelineOptions.MaxChunkCharacters);
        var roslyn = pipelineOptions.Roslyn;
        var maxNeighbors = Math.Max(1, roslyn.MaxNeighborsPerAnchor);
        var snippetChars = Math.Max(200, roslyn.MaxNeighborSnippetCharacters);
        const int contextLines = 12;

        var hunksByFile = ParseDiffHunks(preprocessed.FilteredDiffText);
        if (hunksByFile.Count == 0)
        {
            logger.LogDebug("GraphAwareChunker: no hunks parsed from diff; keeping default chunks.");
            return preprocessed with { Graph = graph };
        }

        var anchors = new Dictionary<string, (string File, string HunkText, int NlStart, int NlEnd)>(StringComparer.Ordinal);
        foreach (var (file, hunks) in hunksByFile)
        {
            foreach (var hunk in hunks)
            {
                var sym = FindAnchorSymbol(graph, file, hunk.NewLineStart, hunk.NewLineEnd);
                if (sym is null)
                {
                    continue;
                }

                if (anchors.TryGetValue(sym.Id, out var existing))
                {
                    anchors[sym.Id] = (
                        file,
                        existing.HunkText + "\n" + hunk.Text,
                        Math.Min(existing.NlStart, hunk.NewLineStart),
                        Math.Max(existing.NlEnd, hunk.NewLineEnd));
                }
                else
                {
                    anchors[sym.Id] = (file, hunk.Text, hunk.NewLineStart, hunk.NewLineEnd);
                }
            }
        }

        if (anchors.Count == 0)
        {
            logger.LogInformation("GraphAwareChunker: no anchor symbols matched graph nodes; using default diff chunks.");
            return preprocessed with { Graph = graph };
        }

        var chunks = new List<string>();
        foreach (var (symbolId, payload) in anchors)
        {
            var symbol = graph.TryGetNode(symbolId);
            if (symbol is null)
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## File: '{payload.File}'");
            sb.AppendLine("### Anchor symbol (Roslyn graph)");
            sb.AppendLine(symbol.Name);
            sb.AppendLine();
            sb.AppendLine("### Changed code (review required)");
            sb.AppendLine(payload.HunkText.Trim());
            sb.AppendLine();
            sb.AppendLine("### Related context (not part of the PR; graph-derived — do not treat as modified)");

            var neighbors = CollectNeighbors(graph, symbol, maxNeighbors, roslyn.IncludeReferenceEdges);
            var budget = maxPrimary - sb.Length;
            foreach (var (edgeKind, node) in neighbors)
            {
                if (budget <= 200 || string.IsNullOrWhiteSpace(node.FilePath))
                {
                    break;
                }

                var center = node.Line ?? 1;
                var relFile = node.FilePath!;
                var snippet = await snippetLoader.TryLoadSnippetAsync(
                    workspaceRoot,
                    relFile,
                    center,
                    contextLines,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(snippet))
                {
                    continue;
                }

                if (snippet.Length > snippetChars)
                {
                    snippet = snippet[..snippetChars] + "…";
                }

                var block = $"#### {edgeKind}: {node.Name}\n{snippet}\n";
                if (block.Length > budget)
                {
                    break;
                }

                sb.AppendLine(block);
                budget -= block.Length;
            }

            chunks.Add(sb.ToString().Trim());
        }

        if (chunks.Count == 0)
        {
            return preprocessed with { Graph = graph };
        }

        var domainChunks = ReviewRiskDomainFormatter.PrependRiskDomainsToChunks(chunks, preprocessed.RiskDomains);
        var reviewChunks = ReviewHintFormatter.AppendHintsToChunks(domainChunks, preprocessed.ReviewHints);

        return preprocessed with
        {
            ReviewChunks = reviewChunks,
            Chunks = SplitByCharacterBudget(reviewChunks, maxPrepared),
            Graph = graph
        };
    }

    private static List<(string EdgeKind, GraphNode Node)> CollectNeighbors(
        CodeGraph g,
        GraphNode anchor,
        int maxNeighbors,
        bool includeReferenceEdges)
    {
        var result = new List<(string EdgeKind, GraphNode Node)>();

        foreach (var edge in g.Incoming(anchor.Id).Where(e => e.Kind == "CALLS"))
        {
            var caller = g.TryGetNode(edge.From);
            if (caller is not null)
            {
                result.Add(("Caller", caller));
            }
        }

        foreach (var edge in g.Outgoing(anchor.Id).Where(e => e.Kind is "CALLS" or "CALLS_CTOR"))
        {
            var callee = g.TryGetNode(edge.To);
            if (callee is not null)
            {
                result.Add((edge.Kind, callee));
            }
        }

        foreach (var edge in g.Incoming(anchor.Id).Where(e => e.Kind == "CONTAINS"))
        {
            var typeNode = g.TryGetNode(edge.From);
            if (typeNode is null)
            {
                continue;
            }

            foreach (var e2 in g.Outgoing(typeNode.Id).Where(e2 => e2.Kind is "IMPLEMENTS" or "INHERITS"))
            {
                var related = g.TryGetNode(e2.To);
                if (related is not null)
                {
                    result.Add((e2.Kind, related));
                }
            }
        }

        if (includeReferenceEdges)
        {
            AddReferenceSiteNeighbors(g, anchor, result);
        }

        foreach (var n in g.Nodes.Where(static n => n.FilePath?.Contains("Tests", StringComparison.OrdinalIgnoreCase) == true).Take(3))
        {
            result.Add(("TestContext", n));
        }

        return result
            .GroupBy(x => x.Node.Id, StringComparer.Ordinal)
            .Select(grp => grp.First())
            .Take(maxNeighbors)
            .ToList();
    }

    /// <summary>
    /// Adds REFERENCED_BY targets (Location nodes) from the anchor's declaring type or the type anchor itself.
    /// Produced only when RoslynGraphBuilder ran with --with-refs.
    /// </summary>
    private static void AddReferenceSiteNeighbors(
        CodeGraph g,
        GraphNode anchor,
        List<(string EdgeKind, GraphNode Node)> result)
    {
        foreach (var typeNode in EnumerateTypesForReferenceLookup(g, anchor))
        {
            foreach (var edge in g.Outgoing(typeNode.Id).Where(e => e.Kind == "REFERENCED_BY"))
            {
                var loc = g.TryGetNode(edge.To);
                if (loc is not null &&
                    string.Equals(loc.Kind, "Location", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(loc.FilePath))
                {
                    result.Add(("RefSite", loc));
                }
            }
        }
    }

    private static IEnumerable<GraphNode> EnumerateTypesForReferenceLookup(CodeGraph g, GraphNode anchor)
    {
        if (string.Equals(anchor.Kind, "NamedType", StringComparison.Ordinal))
        {
            yield return anchor;
            yield break;
        }

        if (!string.Equals(anchor.Kind, "Method", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var edge in g.Incoming(anchor.Id).Where(e => e.Kind == "CONTAINS"))
        {
            var typeNode = g.TryGetNode(edge.From);
            if (typeNode is not null && string.Equals(typeNode.Kind, "NamedType", StringComparison.Ordinal))
            {
                yield return typeNode;
                yield break;
            }
        }
    }

    private static GraphNode? FindAnchorSymbol(CodeGraph graph, string filePath, int nlStart, int nlEnd)
    {
        var normFile = NormalizePath(filePath);
        var candidates = graph.Nodes
            .Where(n =>
                (n.Kind == "Method" || n.Kind == "NamedType") &&
                n.FilePath is not null &&
                n.Line is int line &&
                line <= nlStart &&
                PathsMatch(n.FilePath, normFile))
            .ToList();

        var method = candidates
            .Where(n => n.Kind == "Method")
            .OrderByDescending(n => n.Line ?? 0)
            .FirstOrDefault();
        if (method is not null)
        {
            return method;
        }

        return candidates
            .Where(n => n.Kind == "NamedType")
            .OrderByDescending(n => n.Line ?? 0)
            .FirstOrDefault();
    }

    private static bool PathsMatch(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a))
        {
            return false;
        }

        var na = NormalizePath(a);
        var nb = NormalizePath(b);
        return na.Equals(nb, StringComparison.OrdinalIgnoreCase) ||
               na.EndsWith('/' + nb, StringComparison.OrdinalIgnoreCase) ||
               nb.EndsWith('/' + na, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static IReadOnlyList<string> SplitByCharacterBudget(IReadOnlyList<string> chunks, int maxChars)
    {
        var list = new List<string>();
        foreach (var chunk in chunks)
        {
            if (chunk.Length <= maxChars)
            {
                list.Add(chunk);
                continue;
            }

            for (var i = 0; i < chunk.Length; i += maxChars)
            {
                list.Add(chunk.Substring(i, Math.Min(maxChars, chunk.Length - i)));
            }
        }

        return list;
    }

    private static Dictionary<string, List<DiffHunk>> ParseDiffHunks(string diffText)
    {
        var result = new Dictionary<string, List<DiffHunk>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return result;
        }

        var marker = "diff --git ";
        foreach (var part in diffText.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = (marker + part).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var newPath = ExtractPlusPath(lines);
            if (string.IsNullOrWhiteSpace(newPath))
            {
                continue;
            }

            var hunks = new List<DiffHunk>();
            List<string>? hunkLines = null;
            var currentNewLine = 0;
            var hunkStart = 0;
            var hunkEnd = 0;

            void Flush()
            {
                if (hunkLines is null || hunkLines.Count == 0)
                {
                    return;
                }

                var text = string.Join('\n', hunkLines);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    hunks.Add(new DiffHunk(newPath, hunkStart, Math.Max(hunkStart, hunkEnd), text));
                }

                hunkLines = null;
            }

            foreach (var line in lines)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    Flush();
                    hunkLines = [];
                    currentNewLine = ParseNewLineStart(line) - 1;
                    hunkStart = currentNewLine + 1;
                    hunkEnd = hunkStart;
                    hunkLines.Add(line);
                    continue;
                }

                if (hunkLines is null)
                {
                    continue;
                }

                hunkLines.Add(line);
                if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    currentNewLine++;
                    hunkEnd = currentNewLine;
                }
                else if (line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\\", StringComparison.Ordinal))
                {
                    currentNewLine++;
                }
            }

            Flush();

            if (hunks.Count > 0)
            {
                result[newPath] = hunks;
            }
        }

        return result;
    }

    private static string? ExtractPlusPath(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                return line["+++ b/".Length..].Trim();
            }
        }

        return null;
    }

    private static int ParseNewLineStart(string hunkHeader)
    {
        var plusIndex = hunkHeader.IndexOf('+');
        if (plusIndex < 0)
        {
            return 1;
        }

        var digits = new string(hunkHeader[(plusIndex + 1)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 1;
    }

    private sealed record DiffHunk(string File, int NewLineStart, int NewLineEnd, string Text);
}
