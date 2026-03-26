using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class FindingsNormalizer : IFindingsNormalizer
{
    public IReadOnlyList<ReviewFinding> Normalize(IEnumerable<string> rawResponses)
    {
        var findings = rawResponses
            .SelectMany(ParseResponse)
            .GroupBy(item => $"{item.File}|{item.Title[..Math.Min(item.Title.Length, 24)]}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Description.Length)
                .First())
            .OrderBy(item => item.Severity)
            .ToArray();

        return findings;
    }

    private static IReadOnlyList<ReviewFinding> ParseResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var payload = raw.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
        {
            payload = payload.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().Select(Map).Where(item => item is not null).Cast<ReviewFinding>().ToArray(),
                JsonValueKind.Object when document.RootElement.TryGetProperty("findings", out var findings) =>
                    findings.EnumerateArray().Select(Map).Where(item => item is not null).Cast<ReviewFinding>().ToArray(),
                JsonValueKind.Object => Map(document.RootElement) is { } finding ? [finding] : [],
                _ => []
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ReviewFinding? Map(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = ReadString(element, "file");
        var title = ReadString(element, "title");
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var kind = ReadString(element, "kind");
        var finding = new ReviewFinding(
            file,
            ReadString(element, "line_hint") ?? ReadString(element, "location") ?? "Unknown",
            ParseCategory(ReadString(element, "type")),
            ParseSeverity(ReadString(element, "severity")),
            title,
            ReadString(element, "description") ?? ReadString(element, "problem") ?? string.Empty,
            ReadString(element, "existing_code") ?? ReadString(element, "bad_code") ?? string.Empty,
            ReadString(element, "suggestion") ?? ReadString(element, "fix") ?? string.Empty,
            ReadInt(element, "start_line"),
            ReadInt(element, "end_line"));

        return ShouldKeepFinding(finding, kind) ? finding : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static FindingSeverity ParseSeverity(string? severity)
    {
        return Enum.TryParse<FindingSeverity>(severity, true, out var parsed)
            ? parsed
            : FindingSeverity.Medium;
    }

    private static FindingCategory ParseCategory(string? category)
    {
        return Enum.TryParse<FindingCategory>(category, true, out var parsed)
            ? parsed
            : FindingCategory.Bug;
    }

    private static bool ShouldKeepFinding(ReviewFinding finding, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind) &&
            !kind.Equals("Defect", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("Risk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (finding.Category == FindingCategory.CodeStyle)
        {
            return false;
        }

        return true;
    }
}
