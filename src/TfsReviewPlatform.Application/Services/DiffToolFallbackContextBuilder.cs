using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public static class DiffToolFallbackContextBuilder
{
    private const int MaxResponses = 3;
    private const int DefaultMaxLines = 120;
    private const int MaxReadLines = 300;
    private const int MaxContentLength = 12000;

    public static IReadOnlyList<ReviewWorkspaceToolResponse> Build(
        string diffText,
        IReadOnlyList<ReviewWorkspaceToolRequest> requests,
        IReadOnlyList<ReviewWorkspaceToolResponse> existingResponses)
    {
        if (string.IsNullOrWhiteSpace(diffText) || requests.Count == 0)
        {
            return [];
        }

        var sections = ParseDiffSections(diffText);
        if (sections.Count == 0)
        {
            return [];
        }

        var responses = new List<ReviewWorkspaceToolResponse>();
        var emittedKeys = existingResponses
            .Select(response => $"{NormalizePath(response.FilePath)}|{response.ToolName}|{response.StartLine}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var request in requests)
        {
            if (responses.Count >= MaxResponses)
            {
                break;
            }

            var response = BuildResponse(sections, request);
            if (response is null)
            {
                continue;
            }

            var key = $"{NormalizePath(response.FilePath)}|{response.ToolName}|{response.StartLine}";
            if (!emittedKeys.Add(key))
            {
                continue;
            }

            responses.Add(response);
        }

        return responses;
    }

    private static ReviewWorkspaceToolResponse? BuildResponse(
        IReadOnlyList<DiffFallbackSection> sections,
        ReviewWorkspaceToolRequest request)
    {
        return request.ToolName switch
        {
            "find_files" => BuildFindFilesResponse(sections, request),
            "grep_code" => BuildGrepResponse(sections, request),
            "find_usage" => BuildGrepResponse(sections, request),
            "read_file" => BuildReadFileResponse(sections, request),
            _ => null
        };
    }

    private static ReviewWorkspaceToolResponse? BuildFindFilesResponse(
        IReadOnlyList<DiffFallbackSection> sections,
        ReviewWorkspaceToolRequest request)
    {
        var matches = RankSections(sections, request)
            .Where(item => item.Score >= 80)
            .Take(12)
            .Select(item => item.Section.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        return new ReviewWorkspaceToolResponse(
            request.ToolName,
            "diff-fallback",
            BuildFallbackHeader(request) + "\n" + string.Join('\n', matches.Select(path => $"- {path}")),
            matches[0]);
    }

    private static ReviewWorkspaceToolResponse? BuildGrepResponse(
        IReadOnlyList<DiffFallbackSection> sections,
        ReviewWorkspaceToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return null;
        }

        var hits = RankSections(sections, request)
            .Where(item => item.Score >= 80)
            .SelectMany(item => ExtractLineHits(item.Section, request.Query))
            .Take(20)
            .ToArray();
        if (hits.Length == 0)
        {
            return null;
        }

        var first = hits[0];
        var content = string.Join('\n', hits.Select(hit => $"{hit.FilePath}:{hit.LineNumber}:{hit.Text}"));
        return new ReviewWorkspaceToolResponse(
            request.ToolName,
            "diff-fallback",
            BuildFallbackHeader(request) + "\n" + TrimContent(content),
            first.FilePath,
            first.LineNumber,
            first.LineNumber);
    }

    private static ReviewWorkspaceToolResponse? BuildReadFileResponse(
        IReadOnlyList<DiffFallbackSection> sections,
        ReviewWorkspaceToolRequest request)
    {
        var section = RankSections(sections, request)
            .Where(item => item.Score >= 80)
            .Select(item => item.Section)
            .FirstOrDefault();
        if (section is null)
        {
            return null;
        }

        var excerpt = ExtractNewVersionExcerpt(section, request);
        if (string.IsNullOrWhiteSpace(excerpt.Content))
        {
            return null;
        }

        return new ReviewWorkspaceToolResponse(
            request.ToolName,
            "diff-fallback",
            BuildFallbackHeader(request) + "\n" + excerpt.Content,
            section.FilePath,
            excerpt.StartLine,
            excerpt.EndLine);
    }

    private static IReadOnlyList<(DiffFallbackSection Section, int Score)> RankSections(
        IReadOnlyList<DiffFallbackSection> sections,
        ReviewWorkspaceToolRequest request)
    {
        return sections
            .Select(section => (Section: section, Score: ScoreSection(section, request)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Section.FilePath.Length)
            .ToArray();
    }

    private static int ScoreSection(DiffFallbackSection section, ReviewWorkspaceToolRequest request)
    {
        var score = 0;
        var filePath = NormalizePath(section.FilePath);
        var requestedPath = NormalizePath(request.FilePath);
        var query = request.Query.Trim();
        var pathScope = NormalizePath(request.PathScope);

        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            if (string.Equals(filePath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
            }
            else if (filePath.EndsWith(requestedPath, StringComparison.OrdinalIgnoreCase) ||
                     requestedPath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
            {
                score += 700;
            }
            else if (string.Equals(Path.GetFileName(filePath), Path.GetFileName(requestedPath), StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
            }
        }

        if (!string.IsNullOrWhiteSpace(pathScope) && filePath.Contains(pathScope, StringComparison.OrdinalIgnoreCase))
        {
            score += 120;
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            if (filePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(filePath).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                score += 320;
            }

            if (section.Patch.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                score += 240;
            }

            score += Tokenize(query)
                .Intersect(Tokenize(filePath), StringComparer.OrdinalIgnoreCase)
                .Count() * 20;
        }

        return score;
    }

    private static IReadOnlyList<DiffFallbackSection> ParseDiffSections(string diffText)
    {
        const string marker = "diff --git ";
        var sections = new List<DiffFallbackSection>();
        var parts = diffText.Split(marker, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var patch = marker + part;
            var lines = patch.Split('\n');
            var header = lines.FirstOrDefault() ?? string.Empty;
            var oldPath = ExtractPath(header, "a/");
            var newPath = ExtractPath(header, "b/");
            var isDeleted = lines.Any(line => line.StartsWith("deleted file mode", StringComparison.Ordinal));
            var filePath = isDeleted ? oldPath : newPath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = !string.IsNullOrWhiteSpace(newPath) ? newPath : oldPath;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            sections.Add(new DiffFallbackSection(filePath.Trim(), patch));
        }

        return sections;
    }

    private static string ExtractPath(string header, string marker)
    {
        var start = header.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = header.IndexOf(' ', start);
        return (end > start ? header[start..end] : header[start..]).Trim();
    }

    private static IReadOnlyList<DiffLineHit> ExtractLineHits(DiffFallbackSection section, string query)
    {
        var hits = new List<DiffLineHit>();
        foreach (var line in EnumerateNewVersionLines(section))
        {
            if (line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new DiffLineHit(section.FilePath, line.LineNumber, line.Text.Trim()));
            }
        }

        return hits;
    }

    private static (int StartLine, int EndLine, string Content) ExtractNewVersionExcerpt(
        DiffFallbackSection section,
        ReviewWorkspaceToolRequest request)
    {
        var lines = EnumerateNewVersionLines(section);
        if (lines.Count == 0)
        {
            return (0, 0, string.Empty);
        }

        var maxLines = Math.Clamp(request.MaxLines > 0 ? request.MaxLines : DefaultMaxLines, 20, MaxReadLines);
        var startLine = request.StartLine > 1
            ? request.StartLine
            : FindStartLineNearQuery(lines, request.Query);
        var selected = lines
            .Where(line => line.LineNumber >= startLine)
            .Take(maxLines)
            .ToArray();
        if (selected.Length == 0)
        {
            selected = lines.Take(maxLines).ToArray();
        }

        var content = string.Join('\n', selected.Select(line => $"{line.LineNumber,5}: {line.Text}"));
        return (selected[0].LineNumber, selected[^1].LineNumber, TrimContent(content));
    }

    private static int FindStartLineNearQuery(IReadOnlyList<DiffLineHit> lines, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return lines[0].LineNumber;
        }

        var match = lines.FirstOrDefault(line => line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return lines[0].LineNumber;
        }

        return Math.Max(lines[0].LineNumber, match.LineNumber - 12);
    }

    private static IReadOnlyList<DiffLineHit> EnumerateNewVersionLines(DiffFallbackSection section)
    {
        var lines = new List<DiffLineHit>();
        var currentNewLine = 0;

        foreach (var rawLine in section.Patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.StartsWith("@@", StringComparison.Ordinal))
            {
                currentNewLine = ParseNewLineStart(rawLine) - 1;
                continue;
            }

            if (rawLine.StartsWith("+++", StringComparison.Ordinal) ||
                rawLine.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (rawLine.StartsWith("+", StringComparison.Ordinal))
            {
                currentNewLine++;
                lines.Add(new DiffLineHit(section.FilePath, currentNewLine, rawLine[1..]));
                continue;
            }

            if (rawLine.StartsWith(" ", StringComparison.Ordinal))
            {
                currentNewLine++;
                lines.Add(new DiffLineHit(section.FilePath, currentNewLine, rawLine.Length > 1 ? rawLine[1..] : string.Empty));
            }
        }

        return lines;
    }

    private static int ParseNewLineStart(string hunkHeader)
    {
        var match = Regex.Match(hunkHeader, @"\+(?<start>\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["start"].Value, out var parsed) ? parsed : 1;
    }

    private static string BuildFallbackHeader(ReviewWorkspaceToolRequest request)
    {
        var parts = new List<string>
        {
            "Workspace tool produced no repository result; fallback below is extracted from the changed unified diff only."
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            parts.Add($"query={request.Query}");
        }
        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            parts.Add($"file_path={request.FilePath}");
        }
        if (!string.IsNullOrWhiteSpace(request.PathScope))
        {
            parts.Add($"path_scope={request.PathScope}");
        }

        return string.Join(" ", parts);
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return value
            .Replace('\\', '/')
            .Replace(".", "/", StringComparison.Ordinal)
            .Replace("-", "/", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(SplitIdentifierTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SplitIdentifierTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var current = new List<char>(value.Length);
        foreach (var character in value)
        {
            if (current.Count > 0 &&
                char.IsUpper(character) &&
                (char.IsLower(current[^1]) || (current.Count > 1 && char.IsUpper(current[^1]) && char.IsLower(character))))
            {
                yield return new string(current.ToArray());
                current.Clear();
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Add(char.ToLowerInvariant(character));
            }
            else if (current.Count > 0)
            {
                yield return new string(current.ToArray());
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return new string(current.ToArray());
        }
    }

    private static string TrimContent(string content)
        => content.Length <= MaxContentLength ? content : content[..MaxContentLength];

    private sealed record DiffFallbackSection(string FilePath, string Patch);

    private sealed record DiffLineHit(string FilePath, int LineNumber, string Text);
}
