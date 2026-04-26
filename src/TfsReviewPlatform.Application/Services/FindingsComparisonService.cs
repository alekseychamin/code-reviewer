using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class FindingsComparisonService : IFindingsComparisonService
{
    public FindingsComparisonSnapshot CompareUnchangedDiff(
        Guid? previousRunId,
        IReadOnlyList<ReviewFinding> previousFindings,
        IReadOnlyList<ReviewFinding> currentFindings)
    {
        return new FindingsComparisonSnapshot
        {
            PreviousRunId = previousRunId,
            PreviousFindingsCount = previousFindings.Count,
            CurrentFindingsCount = currentFindings.Count,
            NewFindingsCount = 0,
            StillRelevantFindingsCount = currentFindings.Count,
            ResolvedFindingsCount = 0,
            IsDiffUnchanged = true,
            StillRelevantFindings = Order(currentFindings)
        };
    }

    public FindingsComparisonSnapshot Compare(
        Guid? previousRunId,
        IReadOnlyList<ReviewFinding> previousFindings,
        IReadOnlyList<ReviewFinding> currentFindings)
    {
        if (previousFindings.Count == 0)
        {
            return new FindingsComparisonSnapshot
            {
                PreviousRunId = previousRunId,
                PreviousFindingsCount = 0,
                CurrentFindingsCount = currentFindings.Count,
                NewFindingsCount = currentFindings.Count,
                StillRelevantFindingsCount = 0,
                ResolvedFindingsCount = 0,
                NewFindings = Order(currentFindings)
            };
        }

        var unmatchedPrevious = previousFindings
            .Select((finding, index) => new IndexedFinding(index, finding))
            .ToList();
        var unmatchedCurrent = currentFindings
            .Select((finding, index) => new IndexedFinding(index, finding))
            .ToList();
        var stillRelevant = new List<ReviewFinding>();

        MatchExactFingerprints(unmatchedPrevious, unmatchedCurrent, stillRelevant);
        MatchFuzzyFingerprints(unmatchedPrevious, unmatchedCurrent, stillRelevant);

        return new FindingsComparisonSnapshot
        {
            PreviousRunId = previousRunId,
            PreviousFindingsCount = previousFindings.Count,
            CurrentFindingsCount = currentFindings.Count,
            NewFindingsCount = unmatchedCurrent.Count,
            StillRelevantFindingsCount = stillRelevant.Count,
            ResolvedFindingsCount = unmatchedPrevious.Count,
            NewFindings = Order(unmatchedCurrent.Select(item => item.Finding).ToArray()),
            StillRelevantFindings = Order(stillRelevant),
            ResolvedFindings = Order(unmatchedPrevious.Select(item => item.Finding).ToArray())
        };
    }

    private static void MatchExactFingerprints(
        List<IndexedFinding> unmatchedPrevious,
        List<IndexedFinding> unmatchedCurrent,
        List<ReviewFinding> stillRelevant)
    {
        var previousByFingerprint = unmatchedPrevious
            .GroupBy(item => BuildStrictFingerprint(item.Finding))
            .ToDictionary(group => group.Key, group => new Queue<IndexedFinding>(group));

        var matchedCurrent = new List<IndexedFinding>();
        foreach (var current in unmatchedCurrent)
        {
            var fingerprint = BuildStrictFingerprint(current.Finding);
            if (!previousByFingerprint.TryGetValue(fingerprint, out var bucket) || bucket.Count == 0)
            {
                continue;
            }

            var previous = bucket.Dequeue();
            matchedCurrent.Add(current);
            unmatchedPrevious.Remove(previous);
            stillRelevant.Add(current.Finding);
        }

        foreach (var current in matchedCurrent)
        {
            unmatchedCurrent.Remove(current);
        }
    }

    private static void MatchFuzzyFingerprints(
        List<IndexedFinding> unmatchedPrevious,
        List<IndexedFinding> unmatchedCurrent,
        List<ReviewFinding> stillRelevant)
    {
        var pairs = new List<FindingPairCandidate>();
        foreach (var current in unmatchedCurrent)
        {
            foreach (var previous in unmatchedPrevious)
            {
                var score = ComputePairScore(previous.Finding, current.Finding);
                if (score >= 0.72d)
                {
                    pairs.Add(new FindingPairCandidate(previous, current, score));
                }
            }
        }

        var usedPrevious = new HashSet<int>();
        var usedCurrent = new HashSet<int>();
        foreach (var candidate in pairs.OrderByDescending(item => item.Score))
        {
            if (!usedPrevious.Add(candidate.Previous.Index) || !usedCurrent.Add(candidate.Current.Index))
            {
                continue;
            }

            stillRelevant.Add(candidate.Current.Finding);
        }

        unmatchedPrevious.RemoveAll(item => usedPrevious.Contains(item.Index));
        unmatchedCurrent.RemoveAll(item => usedCurrent.Contains(item.Index));
    }

    private static double ComputePairScore(ReviewFinding previous, ReviewFinding current)
    {
        var previousFile = Normalize(previous.File);
        var currentFile = Normalize(current.File);
        var previousCode = Normalize(previous.ExistingCode);
        var currentCode = Normalize(current.ExistingCode);
        var titleScore = ComputeSimilarity(previous.Title, current.Title);
        var descriptionScore = ComputeSimilarity(previous.Description, current.Description);
        var codeScore = ComputeSimilarity(previous.ExistingCode, current.ExistingCode);

        var score = 0d;
        if (previousFile == currentFile)
        {
            score += 0.4d;
        }
        else if (Path.GetFileName(previousFile) == Path.GetFileName(currentFile))
        {
            score += 0.2d;
        }

        score += titleScore * 0.3d;
        score += descriptionScore * 0.15d;
        score += codeScore * 0.1d;

        if (previous.Category == current.Category)
        {
            score += 0.03d;
        }

        if (previous.Severity == current.Severity)
        {
            score += 0.02d;
        }

        if (previousCode.Length > 0 && previousCode == currentCode)
        {
            score += 0.08d;
        }

        return score;
    }

    private static string BuildStrictFingerprint(ReviewFinding finding)
    {
        return string.Join(
            "|",
            Normalize(finding.File),
            Normalize(finding.Title),
            Normalize(finding.ExistingCode),
            finding.Category,
            finding.Severity);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\\", "/", StringComparison.Ordinal).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized;
    }

    private static double ComputeSimilarity(string? left, string? right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = leftTokens.Intersect(rightTokens).Count();
        if (intersection == 0)
        {
            return 0d;
        }

        return (2d * intersection) / (leftTokens.Count + rightTokens.Count);
    }

    private static HashSet<string> Tokenize(string? value)
    {
        return Regex.Split(Normalize(value), @"[^a-z0-9_]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<ReviewFinding> Order(IEnumerable<ReviewFinding> findings)
    {
        return findings
            .OrderBy(finding => GetSeverityRank(finding.Severity))
            .ThenBy(finding => finding.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetSeverityRank(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.Critical => 0,
            FindingSeverity.High => 1,
            FindingSeverity.Medium => 2,
            _ => 3
        };
    }

    private sealed record IndexedFinding(int Index, ReviewFinding Finding);

    private sealed record FindingPairCandidate(IndexedFinding Previous, IndexedFinding Current, double Score);
}
