using System.Text;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class MarkdownReportBuilder : IMarkdownReportBuilder
{
    public string BuildFullReport(string reviewTitle, string description, IReadOnlyList<ReviewFinding> findings)
    {
        var critical = findings.Count(finding => finding.Severity == FindingSeverity.Critical);
        var high = findings.Count(finding => finding.Severity == FindingSeverity.High);
        var sb = new StringBuilder();
        sb.AppendLine("# AI Code Review Report");
        sb.AppendLine();
        sb.AppendLine($"**Target:** {reviewTitle}");
        sb.AppendLine();
        sb.AppendLine($"**Summary:** {(critical > 0 || high > 0 ? "Needs fixes before merge." : "No blocking issues found by automated review.")}");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine($"**Findings:** {findings.Count} total, {critical} critical, {high} high.");
        sb.AppendLine();

        if (findings.Count == 0)
        {
            sb.AppendLine("No concrete defects were identified. A focused manual review is still recommended for business logic and test intent.");
            return sb.ToString().Trim();
        }

        foreach (var group in findings.GroupBy(finding => finding.File).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();

            foreach (var finding in group.OrderBy(finding => finding.Severity))
            {
                sb.AppendLine($"### {RenderSeverity(finding.Severity)} {finding.Title}");
                sb.AppendLine();
                sb.AppendLine($"- Severity: `{finding.Severity}`");
                sb.AppendLine($"- Category: `{finding.Category}`");
                sb.AppendLine($"- Location: `{finding.LineHint}`");
                sb.AppendLine();
                sb.AppendLine(finding.Description);
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(finding.ExistingCode))
                {
                    sb.AppendLine("```csharp");
                    sb.AppendLine(finding.ExistingCode.Trim());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(finding.Suggestion))
                {
                    sb.AppendLine("Suggested fix:");
                    sb.AppendLine();
                    sb.AppendLine("```csharp");
                    sb.AppendLine(finding.Suggestion.Trim());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString().Trim();
    }

    public string BuildSummaryComment(string reviewTitle, string description, IReadOnlyList<ReviewFinding> findings)
    {
        var critical = findings.Count(finding => finding.Severity == FindingSeverity.Critical);
        var high = findings.Count(finding => finding.Severity == FindingSeverity.High);
        var medium = findings.Count(finding => finding.Severity == FindingSeverity.Medium);
        var status = critical > 0 ? "Needs changes" : high > 0 ? "Review carefully" : "Looks healthy";

        var sb = new StringBuilder();
        sb.AppendLine("## AI Code Review");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {status}");
        sb.AppendLine($"**Target:** {reviewTitle}");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine($"- Critical: {critical}");
        sb.AppendLine($"- High: {high}");
        sb.AppendLine($"- Medium: {medium}");
        sb.AppendLine();

        foreach (var finding in findings
                     .Where(item => item.Severity is FindingSeverity.Critical or FindingSeverity.High)
                     .Take(5))
        {
            sb.AppendLine($"- `{finding.File}`: {finding.Title}");
        }

        if (findings.Count == 0)
        {
            sb.AppendLine("- No concrete findings were generated.");
        }

        return sb.ToString().Trim();
    }

    public IReadOnlyList<InlineCommentDraft> BuildInlineComments(
        IReadOnlyList<ReviewFinding> findings,
        string diffText)
    {
        return findings
            .Where(finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.High)
            .Select(finding =>
            {
                var location = LineLocator.TryLocate(diffText, finding.File, finding.ExistingCode);
                return location is null
                    ? null
                    : new InlineCommentDraft(
                        location.Value.FilePath,
                        location.Value.LineNumber,
                        $"**{finding.Severity}: {finding.Title}**\n\n{finding.Description}");
            })
            .Where(comment => comment is not null)
            .Cast<InlineCommentDraft>()
            .ToArray();
    }

    private static string RenderSeverity(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.Critical => "🔴",
            FindingSeverity.High => "🟠",
            FindingSeverity.Medium => "🟡",
            _ => "🔵"
        };
    }

    private static class LineLocator
    {
        public static (int LineNumber, string FilePath)? TryLocate(string diffText, string targetFile, string snippet)
        {
            if (string.IsNullOrWhiteSpace(targetFile) || string.IsNullOrWhiteSpace(snippet))
            {
                return null;
            }

            var fileName = targetFile.Replace("\\", "/", StringComparison.Ordinal).Split('/').Last().Trim().ToLowerInvariant();
            var snippetLine = snippet.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                ?.Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(snippetLine))
            {
                return null;
            }

            string? currentFile = null;
            var currentLine = 0;

            foreach (var line in diffText.Split('\n'))
            {
                if (line.StartsWith("diff --git", StringComparison.Ordinal))
                {
                    var parts = line.Split(" b/");
                    currentFile = parts.Length == 2 ? parts[1].Trim() : null;
                    continue;
                }

                if (currentFile is null || !currentFile.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    var plusIndex = line.IndexOf('+');
                    if (plusIndex >= 0)
                    {
                        var digits = new string(line[(plusIndex + 1)..].TakeWhile(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out var parsed))
                        {
                            currentLine = parsed - 1;
                        }
                    }

                    continue;
                }

                if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    currentLine++;
                    var normalized = line[1..].Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                    if (normalized.Contains(snippetLine, StringComparison.Ordinal) ||
                        snippetLine.Contains(normalized, StringComparison.Ordinal))
                    {
                        return (currentLine, currentFile.StartsWith('/') ? currentFile : $"/{currentFile}");
                    }

                    continue;
                }

                if (line.StartsWith(' ') || line.StartsWith('\\'))
                {
                    currentLine++;
                }
            }

            return null;
        }
    }
}
