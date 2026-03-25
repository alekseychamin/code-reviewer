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
        var chunks = BuildChunks(reviewContextFiles.Length > 0 ? reviewContextFiles : files.Select(file => file.Content));

        return new PreprocessedDiff
        {
            FilteredDiffText = filteredDiff,
            ReviewContextDiffText = reviewContextDiff,
            ChangedFiles = files.Select(file => file.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Chunks = chunks
        };
    }

    private IReadOnlyList<string> BuildChunks(IEnumerable<string> fileDiffs)
    {
        var maxCharacters = Math.Max(2000, options.Value.MaxChunkCharacters);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var fileDiff in fileDiffs)
        {
            foreach (var fileDiffChunk in SplitOversizedFileDiff(fileDiff, maxCharacters))
            {
                if (current.Length > 0 && current.Length + fileDiffChunk.Length > maxCharacters)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                current.Append(fileDiffChunk);
            }
        }

        if (current.Length > 0)
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
        var effectiveBudget = Math.Max(1000, maxCharacters - header.Length - 1);
        var lineChunks = SplitLinesByBudget(contentLines, effectiveBudget);

        return lineChunks
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
