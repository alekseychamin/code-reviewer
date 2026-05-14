using System.Text;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public static class ReviewRiskDomainFormatter
{
    private const int MaxDomainsPerBlock = 6;
    private const int MaxFactorsPerDomainInBlock = 8;
    private const int MaxDomainsPerChunkBlock = 2;
    private const int MaxFactorsPerDomainInChunkBlock = 2;

    public static string BuildAllRiskDomainsBlock(IReadOnlyList<ReviewRiskDomainInsight> domains)
    {
        return BuildRiskDomainsBlock(
            "=== RISK DOMAIN CLASSIFICATION (apply before reading diff lines) ===",
            domains);
    }

    public static IReadOnlyList<string> PrependRiskDomainsToChunks(
        IReadOnlyList<string> chunks,
        IReadOnlyList<ReviewRiskDomainInsight> domains)
    {
        if (chunks.Count == 0 || domains.Count == 0)
        {
            return chunks;
        }

        return chunks
            .Select(chunk =>
            {
                var filePaths = ExtractChunkFilePaths(chunk);
                var chunkDomains = SelectDomainsForFiles(domains, filePaths);
                var block = BuildCompactChunkRiskDomainsBlock(
                    "### Risk domain classification for this chunk (apply before reading changed lines)",
                    chunkDomains);

                return string.IsNullOrWhiteSpace(block)
                    ? chunk
                    : block + "\n\n" + chunk.TrimStart();
            })
            .ToArray();
    }

    private static string BuildRiskDomainsBlock(
        string title,
        IReadOnlyList<ReviewRiskDomainInsight> domains)
    {
        if (domains.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine("First classify the diff by active domain(s), then inspect the changed lines with the domain checklist. These factors are coverage lenses, not findings by themselves.");

        foreach (var domain in domains
                     .OrderByDescending(domain => domain.Score)
                     .ThenBy(domain => domain.Title, StringComparer.Ordinal)
                     .Take(MaxDomainsPerBlock))
        {
            builder.Append("- ")
                .Append(domain.Title)
                .Append(" (score ")
                .Append(domain.Score)
                .AppendLine(")");
            builder.Append("  Review mode: ").AppendLine(domain.ReviewMode);

            var factors = domain.Factors
                .OrderByDescending(factor => factor.Weight)
                .ThenBy(factor => factor.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(factor => factor.StartLine)
                .Take(MaxFactorsPerDomainInBlock)
                .ToArray();
            if (factors.Length > 0)
            {
                builder.AppendLine("  Determining factors:");
                foreach (var factor in factors)
                {
                    builder.Append("  - ")
                        .Append(factor.Description)
                        .Append(" — ")
                        .Append(factor.FilePath);
                    if (factor.StartLine > 0)
                    {
                        builder.Append(':').Append(factor.StartLine);
                    }

                    builder.Append(" — ").AppendLine(factor.Evidence);
                }
            }

            if (domain.Checklist.Count > 0)
            {
                builder.AppendLine("  Checklist:");
                foreach (var item in domain.Checklist.Take(4))
                {
                    builder.Append("  - ").AppendLine(item);
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCompactChunkRiskDomainsBlock(
        string title,
        IReadOnlyList<ReviewRiskDomainInsight> domains)
    {
        if (domains.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var selectedDomains = domains
                     .OrderByDescending(domain => domain.Score)
                     .ThenBy(domain => domain.Title, StringComparer.Ordinal)
                     .Take(MaxDomainsPerChunkBlock)
                     .ToArray();

        builder.Append(title).Append(": ");
        builder.AppendLine(string.Join(", ", selectedDomains.Select(domain => domain.Title)));
        builder.AppendLine("Lens only; do not emit this classification as a finding.");

        foreach (var domain in selectedDomains)
        {
            var factors = domain.Factors
                .OrderByDescending(factor => factor.Weight)
                .ThenBy(factor => factor.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(factor => factor.StartLine)
                .Take(MaxFactorsPerDomainInChunkBlock)
                .ToArray();
            builder.Append("- ")
                .Append(domain.Title)
                .Append(" signals: ")
                .AppendLine(factors.Length == 0
                    ? "path/content"
                    : string.Join(", ", factors.Select(FormatCompactFactor)));
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<ReviewRiskDomainInsight> SelectDomainsForFiles(
        IReadOnlyList<ReviewRiskDomainInsight> domains,
        IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return domains.Take(MaxDomainsPerBlock).ToArray();
        }

        var selected = new List<ReviewRiskDomainInsight>();
        foreach (var domain in domains)
        {
            var factors = domain.Factors
                .Where(factor => filePaths.Any(filePath => PathsMatch(filePath, factor.FilePath)))
                .ToArray();
            if (factors.Length == 0)
            {
                continue;
            }

            selected.Add(domain with
            {
                Score = factors.Sum(factor => factor.Weight),
                Factors = factors
            });
        }

        return selected.Count > 0
            ? selected
            : domains.Take(MaxDomainsPerChunkBlock).ToArray();
    }

    private static string FormatCompactFactor(ReviewRiskFactor factor)
    {
        var factorName = factor.Id;
        var separatorIndex = factorName.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex >= 0 && separatorIndex + 1 < factorName.Length)
        {
            factorName = factorName[(separatorIndex + 1)..];
        }

        var fileName = Path.GetFileName(factor.FilePath);
        var location = factor.StartLine > 0
            ? $"{fileName}:{factor.StartLine}"
            : fileName;

        return $"{factorName}@{location}";
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
