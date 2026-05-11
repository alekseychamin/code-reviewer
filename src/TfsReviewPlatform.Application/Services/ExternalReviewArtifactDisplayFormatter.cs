using System.Net;
using System.Text.RegularExpressions;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Services;

public static class ExternalReviewArtifactDisplayFormatter
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
        @"```(?<language>[^\r\n`]*)\s*(?<code>.*?)```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LineFragmentRegex = new(
        @"#L(?<start>\d+)(?:-(?<end>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Format(ExternalReviewCommandArtifact command)
    {
        if (string.IsNullOrWhiteSpace(command.Artifact))
        {
            return string.Empty;
        }

        var commandName = command.Command.Trim().TrimStart('/').ToLowerInvariant();
        return commandName switch
        {
            "describe" => FormatDescribe(command.Artifact),
            "review" => FormatReview(command.Artifact),
            _ => CleanText(command.Artifact)
        };
    }

    private static string FormatDescribe(string artifact)
    {
        var prType = ExtractMarkdownSection(artifact, "PR Type");
        var description = ExtractMarkdownSection(artifact, "Description");
        var hasDiagram = MermaidFenceRegex.IsMatch(artifact);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prType))
        {
            parts.Add($"### Тип изменения\n{prType.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add($"### Описание\n{description.Trim()}");
        }

        if (hasDiagram)
        {
            parts.Add("### Диаграмма\nДиаграмма построена и показана в основном блоке описания изменений.");
        }

        return parts.Count > 0
            ? string.Join("\n\n", parts)
            : CleanText(RemoveDetailsBlocks(artifact));
    }

    private static string FormatReview(string artifact)
    {
        var findings = ReviewDetailsRegex.Matches(artifact)
            .Select(RenderReviewFinding)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        if (findings.Length == 0)
        {
            return CleanText(artifact);
        }

        var header = new List<string> { "### Обзор PR-Agent" };
        var effort = ExtractEstimatedEffort(artifact);
        if (!string.IsNullOrWhiteSpace(effort))
        {
            header.Add($"- Оценка сложности ревью: {effort}/5");
        }

        if (artifact.Contains("PR contains tests", StringComparison.OrdinalIgnoreCase))
        {
            header.Add("- Тесты в PR: есть");
        }

        if (artifact.Contains("No security concerns", StringComparison.OrdinalIgnoreCase))
        {
            header.Add("- Риски безопасности: не обнаружены");
        }

        return $"{string.Join('\n', header)}\n\n{string.Join("\n\n", findings)}";
    }

    private static string RenderReviewFinding(Match match)
    {
        var title = CleanText(match.Groups["title"].Value);
        var description = CleanText(match.Groups["summary"].Value);
        var location = ParseLocation(WebUtility.HtmlDecode(match.Groups["url"].Value));
        var code = ExtractCodeBlock(match.Groups["body"].Value, out var language);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var builder = new List<string> { $"#### {title}" };
        if (!string.IsNullOrWhiteSpace(location))
        {
            builder.Add($"Файл: `{location}`");
        }

        builder.Add(description);
        if (!string.IsNullOrWhiteSpace(code))
        {
            builder.Add($"```{language}\n{code}\n```");
        }

        return string.Join("\n\n", builder);
    }

    private static string ExtractMarkdownSection(string markdown, string expectedTitle)
    {
        var withoutDetails = RemoveDetailsBlocks(markdown);
        var matches = MarkdownHeadingRegex.Matches(withoutDetails);
        for (var index = 0; index < matches.Count; index++)
        {
            var title = NormalizeHeading(matches[index].Groups["title"].Value);
            if (!title.Equals(NormalizeHeading(expectedTitle), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : withoutDetails.Length;
            return TrimMarkdownSection(withoutDetails[start..end]);
        }

        return string.Empty;
    }

    private static string RemoveDetailsBlocks(string markdown)
    {
        var start = markdown.IndexOf("<details", StringComparison.OrdinalIgnoreCase);
        return start >= 0 ? markdown[..start] : markdown;
    }

    private static string TrimMarkdownSection(string section)
    {
        var withoutMermaid = MermaidFenceRegex.Replace(section, string.Empty);
        var lines = withoutMermaid
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

    private static string ExtractEstimatedEffort(string artifact)
    {
        var decoded = WebUtility.HtmlDecode(artifact);
        var markerIndex = decoded.IndexOf("Estimated effort to review", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var afterMarker = decoded[(markerIndex + "Estimated effort to review".Length)..];
        var match = Regex.Match(afterMarker, @":\s*(?<value>[1-5])");
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static string ExtractCodeBlock(string value, out string language)
    {
        var match = CodeFenceRegex.Match(value);
        if (!match.Success)
        {
            language = string.Empty;
            return string.Empty;
        }

        language = NormalizeCodeFenceLanguage(match.Groups["language"].Value);
        return WebUtility.HtmlDecode(match.Groups["code"].Value).Trim();
    }

    private static string NormalizeCodeFenceLanguage(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "c#" => "csharp",
            "cs" => "csharp",
            "c++" => "cpp",
            _ => value.Trim()
        };
    }

    private static string ParseLocation(string url)
    {
        var file = ParseFilePath(url);
        var lineMatch = LineFragmentRegex.Match(url);
        if (!lineMatch.Success)
        {
            return file;
        }

        var startLine = lineMatch.Groups["start"].Value;
        var endLine = lineMatch.Groups["end"].Success ? lineMatch.Groups["end"].Value : startLine;
        return startLine == endLine
            ? $"{file}:L{startLine}"
            : $"{file}:L{startLine}-L{endLine}";
    }

    private static string ParseFilePath(string url)
    {
        var path = url.Split('#')[0].Split('?')[0];
        const string marker = "/-/blob/";
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var afterBlob = path[(markerIndex + marker.Length)..];
        var firstSlash = afterBlob.IndexOf('/');
        return firstSlash >= 0 && firstSlash + 1 < afterBlob.Length
            ? Uri.UnescapeDataString(afterBlob[(firstSlash + 1)..])
            : string.Empty;
    }

    private static string CleanText(string value)
    {
        var normalized = value
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
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
                    .Select(line => Regex.Replace(line, @"[ \t]{2,}", " ").Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
    }
}
