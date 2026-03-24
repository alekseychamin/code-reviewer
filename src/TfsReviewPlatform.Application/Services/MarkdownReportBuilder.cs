using System.Text;
using System.Text.RegularExpressions;
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
            .Select(finding =>
            {
                var location = LineLocator.TryLocateByLineHint(diffText, finding.File, finding.LineHint, finding.ExistingCode)
                               ?? LineLocator.TryLocate(diffText, finding.File, finding.ExistingCode)
                               ?? LineLocator.TryLocateFirstChangedLine(diffText, finding.File)
                               ?? (0, NormalizeFilePath(finding.File));

                return new InlineCommentDraft(
                    Guid.NewGuid(),
                    location.FilePath,
                    location.LineNumber,
                    finding.Title,
                    finding.Severity.ToString(),
                    $"**{finding.Severity}: {finding.Title}**\n\n{finding.Description}",
                    finding.ExistingCode,
                    finding.Suggestion,
                    string.Empty,
                    0,
                    0,
                    string.Empty,
                    false,
                    null,
                    [
                        new ReviewCommentMessage(
                            "assistant",
                            $"**{finding.Severity}: {finding.Title}**\n\n{finding.Description}",
                            DateTimeOffset.UtcNow)
                    ]);
            })
            .ToArray();
    }

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "/unknown";
        }

        var normalized = filePath.Replace("\\", "/", StringComparison.Ordinal).Trim();
        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
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
        private static readonly Regex HunkHeaderRegex = new(
            @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@",
            RegexOptions.Compiled);

        private sealed record PatchLine(
            string FilePath,
            int AbsoluteLineNumber,
            string Content,
            bool IsAddition);

        public static (int LineNumber, string FilePath)? TryLocateByLineHint(
            string diffText,
            string targetFile,
            string lineHint,
            string snippet)
        {
            if (string.IsNullOrWhiteSpace(targetFile) || string.IsNullOrWhiteSpace(lineHint))
            {
                return null;
            }

            var match = Regex.Match(lineHint, @"\d+");
            if (!match.Success || !int.TryParse(match.Value, out var absoluteLine) || absoluteLine <= 0)
            {
                return null;
            }

            var patchLines = EnumeratePatchLines(diffText, targetFile).ToArray();
            var directMatch = patchLines.FirstOrDefault(line => line.AbsoluteLineNumber == absoluteLine && line.IsAddition);
            if (directMatch is not null && IsSnippetCompatible(directMatch.Content, snippet))
            {
                return (directMatch.AbsoluteLineNumber, directMatch.FilePath);
            }

            return null;
        }

        public static (int LineNumber, string FilePath)? TryLocate(string diffText, string targetFile, string snippet)
        {
            if (string.IsNullOrWhiteSpace(targetFile) || string.IsNullOrWhiteSpace(snippet))
            {
                return null;
            }

            var candidateLines = ExtractCandidateSnippetLines(snippet);
            if (candidateLines.Count == 0)
            {
                return null;
            }

            PatchLine? bestMatch = null;
            var bestScore = 0d;
            foreach (var patchLine in EnumeratePatchLines(diffText, targetFile))
            {
                foreach (var snippetLine in candidateLines)
                {
                    var score = ComputeSimilarity(snippetLine, patchLine.Content);
                    if (patchLine.IsAddition)
                    {
                        score += 0.03d;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = patchLine;
                    }
                }
            }

            return bestMatch is not null && bestScore >= 0.84d
                ? (bestMatch.AbsoluteLineNumber, bestMatch.FilePath)
                : null;
        }

        public static (int LineNumber, string FilePath)? TryLocateFirstChangedLine(string diffText, string targetFile)
        {
            var firstChangedLine = EnumeratePatchLines(diffText, targetFile).FirstOrDefault(line => line.IsAddition);
            return firstChangedLine is null
                ? null
                : (firstChangedLine.AbsoluteLineNumber, firstChangedLine.FilePath);
        }

        private static IReadOnlyList<string> ExtractCandidateSnippetLines(string snippet)
        {
            return snippet
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Trim().TrimStart('+'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .OrderByDescending(line => NormalizeForMatch(line).Length)
                .Take(3)
                .ToArray();
        }

        private static IEnumerable<PatchLine> EnumeratePatchLines(string diffText, string targetFile)
        {
            if (string.IsNullOrWhiteSpace(diffText) || string.IsNullOrWhiteSpace(targetFile))
            {
                yield break;
            }

            string? currentFile = null;
            var normalizedTarget = NormalizeFilePath(targetFile);
            var currentNewLine = 0;

            foreach (var rawLine in diffText.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("diff --git", StringComparison.Ordinal))
                {
                    currentFile = ExtractDiffFilePath(line);
                    currentNewLine = 0;
                    continue;
                }

                if (!PathsMatch(currentFile, normalizedTarget))
                {
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    currentNewLine = ParseNewLineStart(line) - 1;
                    continue;
                }

                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    currentNewLine++;
                    yield return new PatchLine(NormalizeFilePath(currentFile!), currentNewLine, line[1..], true);
                    continue;
                }

                if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    currentNewLine++;
                    yield return new PatchLine(NormalizeFilePath(currentFile!), currentNewLine, line[1..], false);
                    continue;
                }
            }
        }

        private static string? ExtractDiffFilePath(string diffHeader)
        {
            var marker = " b/";
            var index = diffHeader.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            return NormalizeFilePath(diffHeader[(index + marker.Length)..].Trim());
        }

        private static int ParseNewLineStart(string hunkHeader)
        {
            var match = HunkHeaderRegex.Match(hunkHeader);
            return match.Success && int.TryParse(match.Groups[2].Value, out var parsed)
                ? parsed
                : 1;
        }

        private static bool PathsMatch(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            var normalizedLeft = NormalizeFilePath(left);
            var normalizedRight = NormalizeFilePath(right);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
                   || normalizedLeft.EndsWith(normalizedRight, StringComparison.OrdinalIgnoreCase)
                   || normalizedRight.EndsWith(normalizedLeft, StringComparison.OrdinalIgnoreCase);
        }

        private static double ComputeSimilarity(string left, string right)
        {
            var normalizedLeft = NormalizeForMatch(left);
            var normalizedRight = NormalizeForMatch(right);
            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return 0d;
            }

            if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            {
                return 1d;
            }

            if (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
                normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal))
            {
                return 0.95d;
            }

            var distance = ComputeLevenshteinDistance(normalizedLeft, normalizedRight);
            var maxLength = Math.Max(normalizedLeft.Length, normalizedRight.Length);
            if (maxLength == 0)
            {
                return 0d;
            }

            return 1d - ((double)distance / maxLength);
        }

        private static bool IsSnippetCompatible(string patchLineContent, string snippet)
        {
            var candidates = ExtractCandidateSnippetLines(snippet);
            if (candidates.Count == 0)
            {
                return true;
            }

            var bestScore = candidates
                .Select(candidate => ComputeSimilarity(candidate, patchLineContent))
                .DefaultIfEmpty(0d)
                .Max();

            return bestScore >= 0.84d;
        }

        private static string NormalizeForMatch(string value)
        {
            return new string(value
                .Trim()
                .TrimStart('+')
                .Where(character => !char.IsWhiteSpace(character))
                .ToArray())
                .ToLowerInvariant();
        }

        private static int ComputeLevenshteinDistance(string left, string right)
        {
            var matrix = new int[left.Length + 1, right.Length + 1];
            for (var i = 0; i <= left.Length; i++)
            {
                matrix[i, 0] = i;
            }

            for (var j = 0; j <= right.Length; j++)
            {
                matrix[0, j] = j;
            }

            for (var i = 1; i <= left.Length; i++)
            {
                for (var j = 1; j <= right.Length; j++)
                {
                    var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + substitutionCost);
                }
            }

            return matrix[left.Length, right.Length];
        }
    }
}
