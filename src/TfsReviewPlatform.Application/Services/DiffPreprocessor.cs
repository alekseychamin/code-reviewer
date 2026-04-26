using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public sealed class DiffPreprocessor(IOptions<ReviewPipelineOptions> options) : IDiffPreprocessor
{
    private static readonly HashSet<string> IgnoredExtensions =
    [
        ".lock",
        ".png",
        ".jpg",
        ".jpeg",
        ".svg",
        ".ico",
        ".dll",
        ".pdb",
        ".exe",
        ".min.js",
        ".min.css",
        ".map",
        ".suo",
        ".user"
    ];

    public PreprocessedDiff Process(string diffText)
    {
        var files = SplitDiffByFile(diffText)
            .Where(item => !ShouldIgnore(item.FilePath))
            .ToArray();

        var filteredDiff = string.Concat(files.Select(file => file.Content));
        var reviewContextFiles = files
            .Select(file => ShouldExcludeFromReviewContext(file.FilePath, file.Content)
                ? string.Empty
                : CompressForReviewContext(file.Content))
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToArray();
        var reviewContextDiff = reviewContextFiles.Length > 0
            ? string.Concat(reviewContextFiles)
            : filteredDiff;
        var chunkSource = reviewContextFiles.Length > 0 ? reviewContextFiles : files.Select(file => file.Content);
        var reviewChunks = BuildChunks(
            chunkSource,
            Math.Max(4000, options.Value.MaxPrimaryReviewChunkCharacters),
            mergeFormattedChunks: options.Value.MergePrimaryReviewChunks);
        var preparedChunks = BuildChunks(
            chunkSource,
            Math.Max(2000, options.Value.MaxChunkCharacters),
            mergeFormattedChunks: false);

        return new PreprocessedDiff
        {
            FilteredDiffText = filteredDiff,
            ReviewContextDiffText = reviewContextDiff,
            ChangedFiles = files.Select(file => file.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ReviewChunks = reviewChunks,
            Chunks = preparedChunks
        };
    }

    private IReadOnlyList<string> BuildChunks(
        IEnumerable<string> fileDiffs,
        int maxCharacters,
        bool mergeFormattedChunks)
    {
        maxCharacters = Math.Max(2000, maxCharacters);
        var chunks = new List<string>();
        var current = mergeFormattedChunks ? new StringBuilder() : null;

        foreach (var fileDiff in fileDiffs)
        {
            foreach (var fileDiffChunk in SplitOversizedFileDiff(fileDiff, maxCharacters))
            {
                var formattedChunks = FormatForChunkReview(fileDiffChunk, maxCharacters);

                if (!mergeFormattedChunks)
                {
                    chunks.AddRange(formattedChunks);
                    continue;
                }

                foreach (var formattedChunk in formattedChunks)
                {
                    if (current!.Length > 0 && current.Length + formattedChunk.Length > maxCharacters)
                    {
                        chunks.Add(current.ToString());
                        current.Clear();
                    }

                    current.Append(formattedChunk);
                }
            }
        }

        if (mergeFormattedChunks && current is not null && current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks.Count == 0 ? [string.Empty] : chunks;
    }

    private static IReadOnlyList<string> SplitOversizedFileDiff(string fileDiff, int maxCharacters)
    {
        if (fileDiff.Length <= maxCharacters)
        {
            return [fileDiff];
        }

        var lines = fileDiff.Split('\n');
        var firstHunkIndex = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunkIndex < 0)
        {
            return SplitByCharacterBudget(fileDiff, maxCharacters);
        }

        var header = string.Join('\n', lines[..firstHunkIndex]).TrimEnd('\n');
        var contentLines = lines[firstHunkIndex..];
        var hunks = ExtractHunks(contentLines);
        if (hunks.Count == 0)
        {
            return SplitByCharacterBudget(fileDiff, maxCharacters);
        }

        var effectiveBudget = Math.Max(1000, maxCharacters - header.Length - 1);
        var groupedHunks = GroupHunksByBudget(hunks, effectiveBudget);

        return groupedHunks
            .Select(chunk => string.IsNullOrWhiteSpace(header)
                ? chunk
                : $"{header}\n{chunk}")
            .ToArray();
    }

    private static IReadOnlyList<string> SplitLinesByBudget(IReadOnlyList<string> lines, int maxCharacters)
    {
        var results = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            var normalizedLine = line + '\n';
            if (normalizedLine.Length > maxCharacters)
            {
                if (current.Length > 0)
                {
                    results.Add(current.ToString());
                    current.Clear();
                }

                results.AddRange(SplitByCharacterBudget(normalizedLine, maxCharacters));
                continue;
            }

            if (current.Length > 0 && current.Length + normalizedLine.Length > maxCharacters)
            {
                results.Add(current.ToString());
                current.Clear();
            }

            current.Append(normalizedLine);
        }

        if (current.Length > 0)
        {
            results.Add(current.ToString());
        }

        return results;
    }

    private static IReadOnlyList<string> FormatForChunkReview(string fileDiff, int maxCharacters)
    {
        var lines = fileDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var firstHunkIndex = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunkIndex < 0)
        {
            return SplitByCharacterBudget(fileDiff, maxCharacters);
        }

        var header = lines[..firstHunkIndex];
        var hunks = ExtractHunks(lines[firstHunkIndex..]);
        var filePath = ExtractChunkFilePath(header);
        var chunks = new List<string>();
        var current = new StringBuilder();
        AppendChunkHeader(current, filePath);

        foreach (var hunk in hunks)
        {
            var structuredHunk = BuildStructuredHunkText(hunk);
            if (current.Length > 0 &&
                current.Length > GetChunkHeaderLength(filePath) &&
                current.Length + structuredHunk.Length > maxCharacters)
            {
                chunks.Add(current.ToString());
                current.Clear();
                AppendChunkHeader(current, filePath);
            }

            if (structuredHunk.Length + GetChunkHeaderLength(filePath) > maxCharacters)
            {
                var oversizedHunkChunks = SplitStructuredHunkByBudget(
                        structuredHunk,
                        Math.Max(1000, maxCharacters - GetChunkHeaderLength(filePath)))
                    .Select(part =>
                    {
                        var builder = new StringBuilder();
                        AppendChunkHeader(builder, filePath);
                        builder.Append(part);
                        return builder.ToString();
                    });
                chunks.AddRange(oversizedHunkChunks);
                current.Clear();
                AppendChunkHeader(current, filePath);
                continue;
            }

            current.Append(structuredHunk);
        }

        if (current.Length > GetChunkHeaderLength(filePath))
        {
            chunks.Add(current.ToString());
        }

        return chunks.Count == 0 ? [current.ToString()] : chunks;
    }

    private static int GetChunkHeaderLength(string filePath)
    {
        return $"## File: '{filePath}'\n".Length;
    }

    private static void AppendChunkHeader(StringBuilder builder, string filePath)
    {
        builder.Append("## File: '")
            .Append(filePath)
            .AppendLine("'");
    }

    private static string BuildStructuredHunkText(IReadOnlyList<string> hunk)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine(hunk[0]);

        var scope = ExtractSectionHeader(hunk[0]);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            builder.Append("Context: ")
                .AppendLine(scope);
        }

        var (newHunk, oldHunk) = BuildStructuredHunk(hunk);
        builder.AppendLine("__new hunk__");
        foreach (var line in newHunk)
        {
            builder.AppendLine(line);
        }

        if (oldHunk.Count > 0)
        {
            builder.AppendLine("__old hunk__");
            foreach (var line in oldHunk)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string ExtractChunkFilePath(IReadOnlyList<string> headerLines)
    {
        var diffHeader = headerLines.FirstOrDefault(line => line.StartsWith("diff --git ", StringComparison.Ordinal)) ?? string.Empty;
        var match = Regex.Match(diffHeader, @"^diff --git a/(.+?) b/(.+)$");
        if (match.Success)
        {
            return match.Groups[2].Value.Trim();
        }

        var plusPlusHeader = headerLines.FirstOrDefault(line => line.StartsWith("+++ ", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(plusPlusHeader))
        {
            return plusPlusHeader.Replace("+++ b/", string.Empty, StringComparison.Ordinal)
                .Replace("+++ ", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        return "unknown";
    }

    private static (IReadOnlyList<string> NewHunk, IReadOnlyList<string> OldHunk) BuildStructuredHunk(IReadOnlyList<string> hunk)
    {
        var newLines = new List<string>();
        var oldLines = new List<string>();
        var currentNewLine = ParseNewLineStart(hunk[0]);

        foreach (var line in hunk.Skip(1))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal))
            {
                newLines.Add($"{currentNewLine,4} +{line[1..]}");
                currentNewLine++;
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                oldLines.Add($"-{line[1..]}");
                continue;
            }

            if (line.StartsWith(" ", StringComparison.Ordinal))
            {
                newLines.Add($"{currentNewLine,4}  {line[1..]}");
                oldLines.Add($" {line[1..]}");
                currentNewLine++;
                continue;
            }

            if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                newLines.Add(line);
                if (oldLines.Count > 0)
                {
                    oldLines.Add(line);
                }
            }
        }

        return (newLines, oldLines);
    }

    private static int ParseNewLineStart(string hunkHeader)
    {
        var match = Regex.Match(hunkHeader, @"\+(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value)
            ? value
            : 1;
    }

    private static string ExtractSectionHeader(string hunkHeader)
    {
        var match = Regex.Match(
            hunkHeader,
            @"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@\s*(.*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups[1].Value.Trim();
    }

    private static IReadOnlyList<string> SplitByCharacterBudget(string content, int maxCharacters)
    {
        var results = new List<string>();
        for (var start = 0; start < content.Length; start += maxCharacters)
        {
            var length = Math.Min(maxCharacters, content.Length - start);
            results.Add(content.Substring(start, length));
        }

        return results;
    }

    private static IReadOnlyList<string> SplitStructuredHunkByBudget(string structuredHunk, int maxCharacters)
    {
        var normalized = structuredHunk.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        if (normalized.Length <= maxCharacters)
        {
            return [normalized];
        }

        var lines = normalized.Split('\n');
        var newHunkMarkerIndex = Array.FindIndex(lines, line => line == "__new hunk__");
        if (newHunkMarkerIndex < 0)
        {
            return SplitByCharacterBudget(normalized, maxCharacters);
        }

        var oldHunkMarkerIndex = Array.FindIndex(lines, line => line == "__old hunk__");
        var prefixLines = lines[..(newHunkMarkerIndex + 1)];
        var newLines = oldHunkMarkerIndex >= 0
            ? lines[(newHunkMarkerIndex + 1)..oldHunkMarkerIndex]
            : lines[(newHunkMarkerIndex + 1)..];
        var oldSectionLines = oldHunkMarkerIndex >= 0
            ? lines[oldHunkMarkerIndex..]
            : [];

        var prefix = string.Join('\n', prefixLines).TrimEnd('\n');
        var oldSection = oldSectionLines.Length > 0
            ? "\n" + string.Join('\n', oldSectionLines).TrimEnd('\n')
            : string.Empty;
        var bodyBudget = Math.Max(200, maxCharacters - prefix.Length - oldSection.Length - 1);

        var results = new List<string>();
        var current = new StringBuilder(prefix);

        foreach (var line in newLines)
        {
            var normalizedLine = "\n" + line;
            if (current.Length > prefix.Length && current.Length + normalizedLine.Length > maxCharacters)
            {
                results.Add(current.ToString().TrimEnd());
                current.Clear();
                current.Append(prefix);
            }

            if (current.Length == prefix.Length && normalizedLine.Length > bodyBudget)
            {
                var lineParts = SplitByCharacterBudget(line, bodyBudget);
                foreach (var part in lineParts)
                {
                    if (current.Length > prefix.Length)
                    {
                        results.Add(current.ToString().TrimEnd());
                        current.Clear();
                        current.Append(prefix);
                    }

                    current.Append('\n').Append(part);
                    results.Add(current.ToString().TrimEnd());
                    current.Clear();
                    current.Append(prefix);
                }

                continue;
            }

            current.Append(normalizedLine);
        }

        if (!string.IsNullOrWhiteSpace(oldSection))
        {
            if (current.Length > prefix.Length && current.Length + oldSection.Length > maxCharacters)
            {
                results.Add(current.ToString().TrimEnd());
                current.Clear();
                current.Append(prefix);
            }

            current.Append(oldSection);
        }

        if (current.Length > prefix.Length || results.Count == 0)
        {
            results.Add(current.ToString().TrimEnd());
        }

        return results;
    }

    private static IReadOnlyList<string> GroupHunksByBudget(
        IReadOnlyList<IReadOnlyList<string>> hunks,
        int maxCharacters)
    {
        var results = new List<string>();
        var current = new StringBuilder();
        string? currentScope = null;

        foreach (var hunk in hunks)
        {
            var hunkText = string.Join('\n', hunk).TrimEnd('\n');
            var hunkScope = ExtractSectionHeader(hunk[0]);

            if (hunkText.Length > maxCharacters)
            {
                if (current.Length > 0)
                {
                    results.Add(current.ToString().TrimEnd());
                    current.Clear();
                    currentScope = null;
                }

                results.Add(hunkText);
                continue;
            }

            var needsSplitForBudget = current.Length > 0 && current.Length + 1 + hunkText.Length > maxCharacters;
            var scopeChanged = current.Length > 0 &&
                               !string.IsNullOrWhiteSpace(currentScope) &&
                               !string.IsNullOrWhiteSpace(hunkScope) &&
                               !string.Equals(currentScope, hunkScope, StringComparison.Ordinal) &&
                               current.Length + 1 + hunkText.Length > (maxCharacters * 0.7);

            if (needsSplitForBudget || scopeChanged)
            {
                results.Add(current.ToString().TrimEnd());
                current.Clear();
                currentScope = null;
            }

            if (current.Length > 0)
            {
                current.AppendLine();
            }

            current.Append(hunkText);
            currentScope = string.IsNullOrWhiteSpace(hunkScope) ? currentScope : hunkScope;
        }

        if (current.Length > 0)
        {
            results.Add(current.ToString().TrimEnd());
        }

        return results.Count == 0 ? [string.Empty] : results;
    }

    private static string CompressForReviewContext(string fileDiff)
    {
        var lines = fileDiff.Split('\n');
        var firstHunkIndex = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunkIndex < 0)
        {
            return fileDiff;
        }

        var header = string.Join('\n', lines[..firstHunkIndex]).TrimEnd('\n');
        var hunks = ExtractHunks(lines[firstHunkIndex..]);
        var keptHunks = hunks
            .Where(ContainsAdditions)
            .ToArray();

        if (keptHunks.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(header))
        {
            builder.AppendLine(header);
        }

        foreach (var hunk in keptHunks)
        {
            builder.Append(string.Join('\n', hunk).TrimEnd('\n'));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ExtractHunks(IReadOnlyList<string> hunkLines)
    {
        var results = new List<IReadOnlyList<string>>();
        var current = new List<string>();

        foreach (var line in hunkLines)
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal) && current.Count > 0)
            {
                results.Add(current.ToArray());
                current = [];
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            results.Add(current.ToArray());
        }

        return results;
    }

    private static bool ContainsAdditions(IReadOnlyList<string> hunkLines)
    {
        return hunkLines.Any(line =>
            line.StartsWith('+') &&
            !line.StartsWith("+++", StringComparison.Ordinal));
    }

    private static bool ShouldIgnore(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (IgnoredExtensions.Contains(extension))
        {
            return true;
        }

        var lower = filePath.ToLowerInvariant();
        return lower.Contains("generated", StringComparison.Ordinal) ||
               (lower.Contains("migration", StringComparison.Ordinal) &&
                lower.Contains("snapshot", StringComparison.Ordinal));
    }

    private static bool ShouldExcludeFromReviewContext(string filePath, string content)
    {
        var lowerPath = filePath.ToLowerInvariant();
        var extension = Path.GetExtension(lowerPath);

        if (extension is ".wsdl" or ".xsd" or ".edmx")
        {
            return true;
        }

        if (lowerPath.Contains("/connected services/", StringComparison.Ordinal) ||
            lowerPath.Contains("\\connected services\\", StringComparison.Ordinal) ||
            lowerPath.Contains("/service references/", StringComparison.Ordinal) ||
            lowerPath.Contains("\\service references\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (lowerPath.EndsWith("reference.cs", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".designer.cs", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".generated.cs", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".g.cs", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".g.i.cs", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".assemblyattributes.cs", StringComparison.Ordinal))
        {
            return true;
        }

        var lowerContent = content.ToLowerInvariant();
        return lowerContent.Contains("<auto-generated", StringComparison.Ordinal) ||
               lowerContent.Contains("this code was generated", StringComparison.Ordinal) ||
               lowerContent.Contains("generated by a tool", StringComparison.Ordinal) ||
               lowerContent.Contains("do not modify this code", StringComparison.Ordinal);
    }

    private static IReadOnlyList<(string FilePath, string Content)> SplitDiffByFile(string diffText)
    {
        var sections = Regex.Split(diffText, @"(^diff --git a/.*$)", RegexOptions.Multiline);
        var results = new List<(string FilePath, string Content)>();

        for (var index = 1; index < sections.Length; index += 2)
        {
            var header = sections[index];
            var body = index + 1 < sections.Length ? sections[index + 1] : string.Empty;
            var content = header + body;
            var filePath = ExtractFilePath(header);
            results.Add((filePath, content));
        }

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(diffText))
        {
            results.Add(("unknown.diff", diffText));
        }

        return results;
    }

    private static string ExtractFilePath(string header)
    {
        var marker = " b/";
        var index = header.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? header[(index + marker.Length)..].Trim() : "unknown.diff";
    }
}
