using System.Text.RegularExpressions;

namespace TfsReviewPlatform.Application.Services;

public static partial class MermaidDiagramNormalizer
{
    public static string? Normalize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = StripCodeFences(content.Trim());
        normalized = TrimToDiagramStart(normalized);
        if (!StartsWithSupportedDiagram(normalized))
        {
            return null;
        }

        normalized = ReplaceJsonStyleEscapesInDiagram(normalized);

        return string.Join(
            '\n',
            normalized
                .Split('\n')
                .Select(NormalizeLine))
            .Trim();
    }

    /// <summary>
    /// LLMs often put literal backslash-n inside node labels (invalid or flaky in Mermaid); our prompts ask for &lt;br/&gt; only.
    /// </summary>
    private static string ReplaceJsonStyleEscapesInDiagram(string content)
    {
        return content
            .Replace("\\r\\n", "<br/>", StringComparison.Ordinal)
            .Replace("\\n", "<br/>", StringComparison.Ordinal)
            .Replace("\\r", "<br/>", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal);
    }

    private static string StripCodeFences(string content)
    {
        return content
            .Replace("```mermaid", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string TrimToDiagramStart(string content)
    {
        var graphIndex = content.IndexOf("graph", StringComparison.OrdinalIgnoreCase);
        var flowchartIndex = content.IndexOf("flowchart", StringComparison.OrdinalIgnoreCase);
        var startIndex = graphIndex >= 0 && flowchartIndex >= 0
            ? Math.Min(graphIndex, flowchartIndex)
            : Math.Max(graphIndex, flowchartIndex);

        return startIndex > 0 ? content[startIndex..].Trim() : content;
    }

    private static bool StartsWithSupportedDiagram(string content)
    {
        return content.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
               content.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("end", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("style ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("classDef ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("class ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("linkStyle ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("%%", StringComparison.Ordinal))
        {
            return line;
        }

        var withLegacyTextEdges = LegacyDoubleQuotedTextEdgeRegex().Replace(line, match =>
        {
            var label = SanitizeLabel(match.Groups["text"].Value);
            return $"{match.Groups["indent"].Value}{match.Groups["from"].Value} -->|{label}| {match.Groups["to"].Value}";
        });
        withLegacyTextEdges = LegacySingleQuotedTextEdgeRegex().Replace(withLegacyTextEdges, match =>
        {
            var label = SanitizeLabel(match.Groups["text"].Value);
            return $"{match.Groups["indent"].Value}{match.Groups["from"].Value} -->|{label}| {match.Groups["to"].Value}";
        });

        var withEdgeLabels = EdgeLabelRegex().Replace(withLegacyTextEdges, match =>
        {
            var label = SanitizeLabel(match.Groups["label"].Value);
            return $"{match.Groups["indent"].Value}{match.Groups["from"].Value} {match.Groups["edge"].Value}|{label}| {match.Groups["to"].Value}";
        });

        return NodeLabelRegex().Replace(withEdgeLabels, match =>
        {
            var nodeId = match.Groups["cylinderId"].Success
                ? match.Groups["cylinderId"].Value
                : match.Groups["boxId"].Value;
            var label = match.Groups["cylinderLabel"].Success
                ? match.Groups["cylinderLabel"].Value
                : match.Groups["boxLabel"].Value;

            return $"{nodeId}[\"{SanitizeLabel(label)}\"]";
        });
    }

    private static string SanitizeLabel(string label)
    {
        var normalized = label.Trim();
        if (normalized.Length >= 2 &&
            ((normalized[0] == '"' && normalized[^1] == '"') ||
             (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1];
        }

        normalized = normalized.Replace("`", string.Empty, StringComparison.Ordinal);
        normalized = StripBogusParenQuoteWrappers(normalized);

        return normalized
            .Replace("[]", "()", StringComparison.Ordinal)
            .Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Models sometimes emit labels like ('Name&lt;br/&gt;(detail)' with an extra apostrophe and an unclosed parenthesis from a mistaken (' prefix — invalid in Mermaid.
    /// </summary>
    private static string StripBogusParenQuoteWrappers(string value)
    {
        var s = value.Trim();
        while (s.Length >= 4 && s.StartsWith("('", StringComparison.Ordinal) && s.EndsWith("')", StringComparison.Ordinal))
        {
            s = s[2..^2].Trim();
        }

        while (s.Length >= 4 && s.StartsWith("(\"", StringComparison.Ordinal) && s.EndsWith("\")", StringComparison.Ordinal))
        {
            s = s[2..^2].Trim();
        }

        if (s.Length >= 3 && s.StartsWith("('", StringComparison.Ordinal) && s.EndsWith("'", StringComparison.Ordinal) && !s.EndsWith("')", StringComparison.Ordinal))
        {
            s = s[2..^1].Trim();
        }

        if (s.Length >= 3 && s.StartsWith("(\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal) && !s.EndsWith("\")", StringComparison.Ordinal))
        {
            s = s[2..^1].Trim();
        }

        return s.Trim();
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?<from>[A-Za-z][A-Za-z0-9_]*)\s*--\s*""(?<text>[^""]*)""\s*-->\s*(?<to>[A-Za-z][A-Za-z0-9_]*)\s*$")]
    private static partial Regex LegacyDoubleQuotedTextEdgeRegex();

    [GeneratedRegex(@"^(?<indent>\s*)(?<from>[A-Za-z][A-Za-z0-9_]*)\s*--\s*'(?<text>[^']*)'\s*-->\s*(?<to>[A-Za-z][A-Za-z0-9_]*)\s*$")]
    private static partial Regex LegacySingleQuotedTextEdgeRegex();

    [GeneratedRegex(@"^(?<indent>\s*)(?<from>[A-Za-z][A-Za-z0-9_]*)\s*(?<edge>-->|---|-.->|==>)\s*(?<to>[A-Za-z][A-Za-z0-9_]*)\s*:\s*(?<label>.+?)\s*$")]
    private static partial Regex EdgeLabelRegex();

    [GeneratedRegex(@"\b(?<cylinderId>[A-Za-z][A-Za-z0-9_]*)\s*\[\((?<cylinderLabel>.*?)\)\]|\b(?<boxId>[A-Za-z][A-Za-z0-9_]*)\s*\[(?<boxLabel>.*?)\]")]
    private static partial Regex NodeLabelRegex();
}
