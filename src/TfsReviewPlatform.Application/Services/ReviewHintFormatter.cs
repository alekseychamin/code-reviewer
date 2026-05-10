using System.Text;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public static class ReviewHintFormatter
{
    public static string BuildAllHintsBlock(IReadOnlyList<ReviewHint> hints)
    {
        if (hints.Count == 0)
        {
            return string.Empty;
        }

        return BuildHintsBlock("=== DETERMINISTIC REVIEW HINTS (candidate checks, verify before reporting) ===", hints);
    }

    public static string BuildFileHintsBlock(string filePath, IReadOnlyList<ReviewHint> hints)
    {
        var fileHints = hints
            .Where(hint => PathsMatch(hint.FilePath, filePath))
            .ToArray();

        return fileHints.Length == 0
            ? string.Empty
            : BuildHintsBlock("### Deterministic review hints for this file (candidate checks, verify before reporting)", fileHints);
    }

    public static IReadOnlyList<string> AppendHintsToChunks(
        IReadOnlyList<string> chunks,
        IReadOnlyList<ReviewHint> hints)
    {
        if (chunks.Count == 0 || hints.Count == 0)
        {
            return chunks;
        }

        return chunks
            .Select(chunk =>
            {
                var filePaths = ExtractChunkFilePaths(chunk);
                var chunkHints = hints
                    .Where(hint => filePaths.Any(filePath => PathsMatch(hint.FilePath, filePath)))
                    .ToArray();
                if (chunkHints.Length == 0)
                {
                    return chunk;
                }

                return chunk.TrimEnd() + "\n\n" +
                       BuildHintsBlock("### Deterministic review hints for changed file(s)", chunkHints);
            })
            .ToArray();
    }

    private static string BuildHintsBlock(string title, IReadOnlyList<ReviewHint> hints)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine("These hints are not findings by themselves. Use them as a checklist and emit a finding only when the diff or supplemental context confirms the issue.");

        foreach (var hint in hints)
        {
            builder.Append("- ")
                .Append(hint.RuleId)
                .Append(" [")
                .Append(hint.Category)
                .Append("] ")
                .Append(hint.FilePath);

            if (hint.StartLine > 0)
            {
                builder.Append(':').Append(hint.StartLine);
            }

            builder.Append(" — ").AppendLine(hint.Message);

            if (!string.IsNullOrWhiteSpace(hint.Evidence))
            {
                builder.Append("  Evidence: ").AppendLine(hint.Evidence);
            }

            if (!string.IsNullOrWhiteSpace(hint.SuggestedVerification))
            {
                builder.Append("  Verify: ").AppendLine(hint.SuggestedVerification);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ExtractChunkFilePaths(string chunk)
    {
        var results = new List<string>();
        foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string marker = "## File: '";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOf('\'', marker.Length);
            if (end > marker.Length)
            {
                results.Add(line[marker.Length..end]);
            }
        }

        return results;
    }

    private static bool PathsMatch(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.EndsWith('/' + normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.EndsWith('/' + normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/').Replace('\\', '/');
    }
}
