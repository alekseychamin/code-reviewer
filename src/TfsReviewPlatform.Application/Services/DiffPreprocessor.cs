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
        var reviewHints = BuildReviewHints(files);
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
        var reviewChunks = ReviewHintFormatter.AppendHintsToChunks(
            BuildChunks(
                chunkSource,
                Math.Max(4000, options.Value.MaxPrimaryReviewChunkCharacters),
                mergeFormattedChunks: options.Value.MergePrimaryReviewChunks),
            reviewHints);
        var preparedChunks = ReviewHintFormatter.AppendHintsToChunks(
            BuildChunks(
                chunkSource,
                Math.Max(2000, options.Value.MaxChunkCharacters),
                mergeFormattedChunks: false),
            reviewHints);

        return new PreprocessedDiff
        {
            FilteredDiffText = filteredDiff,
            ReviewContextDiffText = reviewContextDiff,
            ChangedFiles = files.Select(file => file.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ReviewChunks = reviewChunks,
            Chunks = preparedChunks,
            ReviewHints = reviewHints
        };
    }

    private static IReadOnlyList<ReviewHint> BuildReviewHints(
        IReadOnlyList<(string FilePath, string Content)> files)
    {
        var hints = new List<ReviewHint>();

        foreach (var file in files)
        {
            var diffLines = EnumerateDiffLines(file.Content);
            if (diffLines.Count == 0)
            {
                continue;
            }

            if (file.FilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                AddSqlHints(file.FilePath, diffLines, hints);
            }

            if (file.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                AddCSharpDataIntegrityHints(file.FilePath, diffLines, hints);
            }

            if (IsTestFile(file.FilePath))
            {
                AddTestAndSeedHints(file.FilePath, diffLines, hints);
            }
        }

        AddOptionsBindingHints(files, hints);

        return hints
            .DistinctBy(hint => $"{hint.RuleId}|{hint.FilePath}|{hint.StartLine}|{hint.Message}")
            .Take(40)
            .ToArray();
    }

    private static void AddSqlHints(
        string filePath,
        IReadOnlyList<DiffLine> diffLines,
        List<ReviewHint> hints)
    {
        var newLines = diffLines
            .Where(line => line.Kind is '+' or ' ')
            .ToArray();
        var removedLines = diffLines
            .Where(line => line.Kind == '-')
            .ToArray();
        var newText = string.Join('\n', newLines.Select(line => line.Text));
        var removedPredicateLines = removedLines
            .Where(line =>
                line.Text.Contains("IsBasic", StringComparison.OrdinalIgnoreCase) ||
                line.Text.Contains("IsBcAllowed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (removedPredicateLines.Length > 0 &&
            (!newText.Contains("IsBasic", StringComparison.OrdinalIgnoreCase) ||
             !newText.Contains("IsBcAllowed", StringComparison.OrdinalIgnoreCase)))
        {
            var firstRemoved = removedPredicateLines[0];
            hints.Add(new ReviewHint
            {
                RuleId = "SQL_REMOVED_BUSINESS_FILTER",
                Category = "SQL/DataIntegrity",
                FilePath = filePath,
                StartLine = NearestNewLine(diffLines, firstRemoved),
                Message = "Predicate lines containing IsBasic/IsBcAllowed were removed or are no longer visible in the new query.",
                Evidence = string.Join(" | ", removedPredicateLines.Select(line => line.Text.Trim()).Take(3)),
                SuggestedVerification = "Compare old vs new query semantics and verify whether orders from non-basic or non-BC-allowed regions must still be filtered."
            });
        }

        var aliasesWithRemovedUsage = removedPredicateLines
            .SelectMany(line => Regex.Matches(line.Text, @"\b(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*\.", RegexOptions.CultureInvariant)
                .Select(match => match.Groups["alias"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddUnusedSqlJoinHints(filePath, newLines, aliasesWithRemovedUsage, hints);
    }

    private static void AddUnusedSqlJoinHints(
        string filePath,
        IReadOnlyList<DiffLine> newLines,
        IReadOnlySet<string> aliasesWithRemovedUsage,
        List<ReviewHint> hints)
    {
        if (aliasesWithRemovedUsage.Count == 0)
        {
            return;
        }

        for (var index = 0; index < newLines.Count; index++)
        {
            var line = newLines[index];
            var match = Regex.Match(
                line.Text,
                @"\b(?:left\s+join|inner\s+join|right\s+join|full\s+join|join)\s+(?:""[^""]+""|[\w.]+)\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var alias = match.Groups["alias"].Value;
            if (!aliasesWithRemovedUsage.Contains(alias))
            {
                continue;
            }

            var joinBlockEnd = FindSqlJoinBlockEnd(newLines, index + 1);
            var aliasUsagePattern = new Regex(@"\b" + Regex.Escape(alias) + @"\s*\.", RegexOptions.CultureInvariant);
            var usedOutsideJoinBlock = newLines
                .Where((_, lineIndex) => lineIndex < index || lineIndex >= joinBlockEnd)
                .Any(candidate => aliasUsagePattern.IsMatch(candidate.Text));

            if (usedOutsideJoinBlock)
            {
                continue;
            }

            hints.Add(new ReviewHint
            {
                RuleId = "SQL_JOIN_ALIAS_ONLY_USED_IN_JOIN",
                Category = "SQL/Cardinality",
                FilePath = filePath,
                StartLine = line.NewLine,
                Message = $"Join alias '{alias}' was used by removed predicates and is now visible only inside its own join block in the changed hunk.",
                Evidence = line.Text.Trim(),
                SuggestedVerification = "Verify whether the join is now unused. If the joined table can have multiple rows per key, the query may duplicate result rows."
            });
        }
    }

    private static int FindSqlJoinBlockEnd(IReadOnlyList<DiffLine> lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Count; index++)
        {
            var text = lines[index].Text.TrimStart();
            if (Regex.IsMatch(
                    text,
                    @"^(left\s+join|inner\s+join|right\s+join|full\s+join|join|where|group\s+by|order\s+by|having|limit|union)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static void AddCSharpDataIntegrityHints(
        string filePath,
        IReadOnlyList<DiffLine> diffLines,
        List<ReviewHint> hints)
    {
        var newLines = diffLines
            .Where(line => line.Kind is '+' or ' ')
            .ToArray();

        for (var index = 0; index < newLines.Length; index++)
        {
            var text = newLines[index].Text;
            if (!text.Contains(".GroupBy", StringComparison.Ordinal))
            {
                continue;
            }

            var window = newLines
                .Skip(index)
                .Take(14)
                .Select(line => line.Text)
                .ToArray();
            var windowText = string.Join('\n', window);
            if (!windowText.Contains(".First(", StringComparison.Ordinal) &&
                !windowText.Contains(".First()", StringComparison.Ordinal))
            {
                continue;
            }

            if (windowText.Contains(".OrderBy", StringComparison.Ordinal) ||
                windowText.Contains(".ThenBy", StringComparison.Ordinal))
            {
                continue;
            }

            hints.Add(new ReviewHint
            {
                RuleId = "GROUP_BY_FIRST_WITHOUT_ORDER",
                Category = "DataIntegrity",
                FilePath = filePath,
                StartLine = newLines[index].NewLine,
                Message = "GroupBy is followed by First() without a visible deterministic ordering.",
                Evidence = string.Join(" | ", window.Select(item => item.Trim()).Take(6)),
                SuggestedVerification = "Verify duplicate-key behavior. If several records share the grouping key, choose a canonical row with a predicate or explicit OrderBy."
            });
        }

        AddNullableContractHints(filePath, newLines, hints);
    }

    private static void AddNullableContractHints(
        string filePath,
        IReadOnlyList<DiffLine> newLines,
        List<ReviewHint> hints)
    {
        for (var index = 0; index < newLines.Count; index++)
        {
            if (!Regex.IsMatch(newLines[index].Text, @"\breturn\s+null\s*;", RegexOptions.CultureInvariant))
            {
                continue;
            }

            var signature = newLines
                .Take(index)
                .Reverse()
                .Take(10)
                .FirstOrDefault(line => LooksLikeNonNullableMethodSignature(line.Text));
            if (signature is null)
            {
                continue;
            }

            hints.Add(new ReviewHint
            {
                RuleId = "NON_NULLABLE_CONTRACT_RETURNS_NULL",
                Category = "Contract",
                FilePath = filePath,
                StartLine = newLines[index].NewLine,
                Message = "A visible non-nullable method signature is close to a return null path.",
                Evidence = $"{signature.Text.Trim()} ... {newLines[index].Text.Trim()}",
                SuggestedVerification = "Verify nullable annotations and update the public/interface contract to nullable when null is an expected result."
            });
        }
    }

    private static bool LooksLikeNonNullableMethodSignature(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.Contains('(', StringComparison.Ordinal) ||
            trimmed.Contains("=>", StringComparison.Ordinal) ||
            trimmed.Contains("?", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(
            trimmed,
            @"\b(public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?(?:string|[A-Z][A-Za-z0-9_<>.,\s]*)\s+[A-Za-z_][A-Za-z0-9_]*\s*\(",
            RegexOptions.CultureInvariant);
    }

    private static void AddTestAndSeedHints(
        string filePath,
        IReadOnlyList<DiffLine> diffLines,
        List<ReviewHint> hints)
    {
        var seedReferences = diffLines
            .Where(line => line.Kind is '+' or ' ')
            .SelectMany(line => Regex.Matches(
                    line.Text,
                    @"\b(?<seed>Seed[A-Za-z0-9_]+)\.(?<member>[A-Za-z0-9_]+)\b",
                    RegexOptions.CultureInvariant)
                .Select(match => (Line: line, Seed: match.Groups["seed"].Value, Member: match.Groups["member"].Value)))
            .DistinctBy(item => $"{item.Seed}.{item.Member}")
            .Take(6)
            .ToArray();

        foreach (var item in seedReferences)
        {
            hints.Add(new ReviewHint
            {
                RuleId = "TEST_ASSERTION_USES_SEED_MEMBER",
                Category = "Tests/Seeds",
                FilePath = filePath,
                StartLine = item.Line.NewLine,
                Message = $"Test uses {item.Seed}.{item.Member}; seed data should contain the matching row/value.",
                Evidence = item.Line.Text.Trim(),
                SuggestedVerification = $"Read {item.Seed} and the target production query to confirm the test fixture can produce the expected result."
            });
        }
    }

    private static void AddOptionsBindingHints(
        IReadOnlyList<(string FilePath, string Content)> files,
        List<ReviewHint> hints)
    {
        var optionBindings = files
            .Where(file => file.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => EnumerateDiffLines(file.Content)
                .Where(line => line.Kind is '+' or ' ')
                .Select(line => new
                {
                    file.FilePath,
                    Line = line,
                    Match = Regex.Match(
                        line.Text,
                        @"Configure<(?<type>[A-Za-z0-9_]+)>\s*\(\s*configuration\.GetSection\(\s*""(?<section>[^""]+)""\s*\)",
                        RegexOptions.CultureInvariant)
                })
                .Where(item => item.Match.Success))
            .ToArray();
        if (optionBindings.Length == 0)
        {
            return;
        }

        var appsettingsFiles = files
            .Where(file => Path.GetFileName(file.FilePath).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                           file.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var visibleConfigKeys = appsettingsFiles
            .SelectMany(file => EnumerateDiffLines(file.Content)
                .Where(line => line.Kind is '+' or ' ')
                .SelectMany(line => Regex.Matches(line.Text, @"""(?<key>[^""]+)""\s*:", RegexOptions.CultureInvariant)
                    .Select(match => match.Groups["key"].Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in optionBindings)
        {
            var section = binding.Match.Groups["section"].Value;
            if (visibleConfigKeys.Contains(section))
            {
                continue;
            }

            hints.Add(new ReviewHint
            {
                RuleId = "OPTIONS_SECTION_NOT_VISIBLE_IN_CHANGED_CONFIG",
                Category = "Configuration/DI",
                FilePath = binding.FilePath,
                StartLine = binding.Line.NewLine,
                Message = $"Options are bound from section '{section}', but changed appsettings hunks do not show that section.",
                Evidence = binding.Line.Text.Trim(),
                SuggestedVerification = "Read appsettings*.json and environment conventions. If option keys live under another section, runtime configuration overrides will not bind."
            });
        }
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

    private static IReadOnlyList<DiffLine> EnumerateDiffLines(string fileDiff)
    {
        var lines = fileDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var firstHunkIndex = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunkIndex < 0)
        {
            return [];
        }

        var results = new List<DiffLine>();
        foreach (var hunk in ExtractHunks(lines[firstHunkIndex..]))
        {
            if (hunk.Count == 0)
            {
                continue;
            }

            var currentNewLine = ParseNewLineStart(hunk[0]);
            foreach (var line in hunk.Skip(1))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    results.Add(new DiffLine('+', currentNewLine, line[1..]));
                    currentNewLine++;
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    results.Add(new DiffLine('-', 0, line[1..]));
                    continue;
                }

                if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    results.Add(new DiffLine(' ', currentNewLine, line[1..]));
                    currentNewLine++;
                }
            }
        }

        return results;
    }

    private static int NearestNewLine(IReadOnlyList<DiffLine> lines, DiffLine removedLine)
    {
        var index = -1;
        for (var cursor = 0; cursor < lines.Count; cursor++)
        {
            if (EqualityComparer<DiffLine>.Default.Equals(lines[cursor], removedLine))
            {
                index = cursor;
                break;
            }
        }

        if (index < 0)
        {
            return 1;
        }

        for (var cursor = index + 1; cursor < lines.Count; cursor++)
        {
            if (lines[cursor].NewLine > 0)
            {
                return lines[cursor].NewLine;
            }
        }

        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            if (lines[cursor].NewLine > 0)
            {
                return lines[cursor].NewLine;
            }
        }

        return 1;
    }

    private static bool IsTestFile(string filePath)
    {
        return filePath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/TestcontainersTests/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("\\TestcontainersTests\\", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
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

    private sealed record DiffLine(char Kind, int NewLine, string Text);
}
