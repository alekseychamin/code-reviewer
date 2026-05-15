using System.Net;
using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ExternalReviewArtifactParser : IExternalReviewArtifactParser
{
    private static readonly Regex MarkdownHeadingRegex = new(
        @"^###\s+\*{0,2}(?<title>[^*\r\n]+?)\*{0,2}\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MermaidFenceRegex = new(
        @"```mermaid\s*(?<diagram>.*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ReviewDetailsRegex = new(
        @"<details>\s*<summary>\s*<a\s+href=['""](?<url>[^'""]+)['""]>\s*<strong>(?<title>.*?)</strong>\s*</a>(?<summary>.*?)</summary>(?<body>.*?)</details>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex CodeFenceRegex = new(
        @"```[^\r\n]*\s*(?<code>.*?)```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LineFragmentRegex = new(
        @"#L(?<start>\d+)(?:-(?<end>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ExternalReviewInsights Parse(ExternalReviewArtifact artifact)
    {
        if (!artifact.Attempted || artifact.Commands.Count == 0)
        {
            return ExternalReviewInsights.Empty;
        }

        var describeArtifact = FindCommandArtifact(artifact, "describe");
        var reviewArtifact = FindCommandArtifact(artifact, "review");

        return new ExternalReviewInsights
        {
            ChangeSummary = ParseChangeSummary(describeArtifact),
            Findings = ParseReviewFindings(reviewArtifact)
        };
    }

    private static string? FindCommandArtifact(ExternalReviewArtifact artifact, string commandName)
    {
        return artifact.Commands
            .FirstOrDefault(command =>
                command.Succeeded &&
                command.Command.Trim().TrimStart('/').Equals(commandName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(command.Artifact))
            ?.Artifact;
    }

    private static ChangeSummaryResult? ParseChangeSummary(string? artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact))
        {
            return null;
        }

        var prType = ExtractMarkdownSection(artifact, "PR Type");
        var description = ExtractMarkdownSection(artifact, "Description");
        var diagram = ExtractMermaidDiagram(artifact);
        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(diagram))
        {
            return null;
        }

        var renderedDescription = RenderDescription(prType, description);
        return new ChangeSummaryResult
        {
            Description = renderedDescription,
            DiagramMermaid = diagram,
            StructuredContent = new ChangeDescriptionStructuredContent
            {
                Category = prType,
                Summary = StripMarkdown(description),
                EstimatedReviewEffort = null
            }
        };
    }

    private static IReadOnlyList<ReviewFinding> ParseReviewFindings(string? artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact))
        {
            return [];
        }

        var findings = new List<ReviewFinding>();
        foreach (Match match in ReviewDetailsRegex.Matches(artifact))
        {
            var title = CleanText(match.Groups["title"].Value);
            var description = CleanText(match.Groups["summary"].Value);
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var body = match.Groups["body"].Value;
            var location = ParseLocation(url);
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(description) ||
                string.IsNullOrWhiteSpace(location.File))
            {
                continue;
            }

            var existingCode = ExtractCodeBlock(body);
            findings.Add(new ReviewFinding(
                location.File,
                BuildLineHint(location.StartLine, location.EndLine),
                ClassifyCategory($"{title} {description}"),
                ClassifySeverity($"{title} {description}"),
                ReviewFindingSource.ExternalReview,
                title,
                description,
                existingCode,
                BuildSuggestion(title, description),
                location.StartLine,
                location.EndLine,
                Guid.NewGuid()));
        }

        return findings;
    }

    private static string ExtractMarkdownSection(string markdown, string expectedTitle)
    {
        var matches = MarkdownHeadingRegex.Matches(markdown);
        for (var index = 0; index < matches.Count; index++)
        {
            var title = NormalizeHeading(matches[index].Groups["title"].Value);
            if (!title.Equals(NormalizeHeading(expectedTitle), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : markdown.Length;
            var section = markdown[start..end];
            var detailsIndex = section.IndexOf("<details", StringComparison.OrdinalIgnoreCase);
            if (detailsIndex >= 0)
            {
                section = section[..detailsIndex];
            }

            return TrimMarkdownSection(section);
        }

        return string.Empty;
    }

    private static string ExtractMermaidDiagram(string markdown)
    {
        var match = MermaidFenceRegex.Match(markdown);
        return match.Success
            ? MermaidDiagramNormalizer.Normalize(match.Groups["diagram"].Value) ?? string.Empty
            : string.Empty;
    }

    private static string RenderDescription(string prType, string description)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prType))
        {
            parts.Add($"### PR Type\n{prType.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add($"### Description\n{description.Trim()}");
        }

        return parts.Count == 0
            ? "Описание изменений подготовлено PR-Agent."
            : string.Join("\n\n", parts);
    }

    private static string TrimMarkdownSection(string section)
    {
        var lines = section
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line =>
            {
                var trimmed = line.Trim();
                return trimmed is not ("___" or "---");
            })
            .ToArray();

        return string.Join('\n', lines).Trim();
    }

    private static string NormalizeHeading(string value)
    {
        return value.Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string StripMarkdown(string value)
    {
        return string.Join(
                '\n',
                value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Select(line => line.Trim().TrimStart('-', '*').Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
    }

    private static string CleanText(string value)
    {
        var normalized = value
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        normalized = HtmlTagRegex.Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(normalized);
        return string.Join(
                "\n",
                normalized.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
    }

    private static string ExtractCodeBlock(string body)
    {
        var match = CodeFenceRegex.Match(body);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["code"].Value).Trim()
            : string.Empty;
    }

    private static (string File, int StartLine, int EndLine) ParseLocation(string url)
    {
        var startLine = 0;
        var endLine = 0;
        var lineMatch = LineFragmentRegex.Match(url);
        if (lineMatch.Success)
        {
            startLine = int.Parse(lineMatch.Groups["start"].Value);
            endLine = lineMatch.Groups["end"].Success
                ? int.Parse(lineMatch.Groups["end"].Value)
                : startLine;
        }

        var path = url.Split('#')[0].Split('?')[0];
        const string marker = "/-/blob/";
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return (string.Empty, startLine, endLine);
        }

        var afterBlob = path[(markerIndex + marker.Length)..];
        var firstSlash = afterBlob.IndexOf('/');
        if (firstSlash < 0 || firstSlash + 1 >= afterBlob.Length)
        {
            return (string.Empty, startLine, endLine);
        }

        return (Uri.UnescapeDataString(afterBlob[(firstSlash + 1)..]), startLine, endLine);
    }

    private static string BuildLineHint(int startLine, int endLine)
    {
        if (startLine <= 0)
        {
            return "PR-Agent";
        }

        return endLine > startLine
            ? $"L{startLine}-L{endLine}"
            : $"L{startLine}";
    }

    private static FindingCategory ClassifyCategory(string text)
    {
        if (ContainsAny(text, "security", "безопас"))
        {
            return FindingCategory.Security;
        }

        if (ContainsAny(text, "performance", "производит", "медлен", "индекс"))
        {
            return FindingCategory.Performance;
        }

        if (ContainsAny(text, "architecture", "архитект"))
        {
            return FindingCategory.Architecture;
        }

        if (ContainsAny(text, "reliability", "надёж", "надеж", "timeout", "race"))
        {
            return FindingCategory.Reliability;
        }

        return ContainsAny(text, "логик", "filter", "фильтр", "condition", "услов")
            ? FindingCategory.Logic
            : FindingCategory.Bug;
    }

    private static FindingSeverity ClassifySeverity(string text)
    {
        if (ContainsAny(text, "critical", "критичес"))
        {
            return FindingSeverity.Critical;
        }

        if (ContainsAny(text, "high", "серьез", "серьёз", "security", "безопас"))
        {
            return FindingSeverity.High;
        }

        return FindingSeverity.Medium;
    }

    private static string BuildSuggestion(string title, string description)
    {
        var text = $"{title} {description}";
        if (ContainsAny(text, "isbasic", "isbcallowed"))
        {
            return "Вернуть эквивалентную фильтрацию IsBasic/IsBcAllowed либо явно подтвердить, что новое поведение ожидаемо и покрыто тестами.";
        }

        return "Проверить замечание PR-Agent: исправить риск или явно зафиксировать, почему новое поведение ожидаемо.";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
