using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ChunkReviewResponseParser : IChunkReviewResponseParser
{
    public (IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<ReviewOpportunityItem> Opportunities) ParseChunkResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], []);
        }

        var payload = NormalizeJsonPayload(raw);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ([], []);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return (ParseFindingsJsonArray(root), []);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ([], []);
            }

            var findings = root.TryGetProperty("findings", out var findingsNode) && findingsNode.ValueKind == JsonValueKind.Array
                ? ParseFindingsJsonArray(findingsNode)
                : MapFinding(root) is { } single ? [single] : [];

            var opportunities = root.TryGetProperty("opportunities", out var opportunitiesNode) && opportunitiesNode.ValueKind == JsonValueKind.Array
                ? opportunitiesNode.EnumerateArray().Select(MapOpportunity).Where(item => item is not null).Cast<ReviewOpportunityItem>().ToArray()
                : [];

            return (findings, opportunities);
        }
        catch (JsonException)
        {
            return ([], []);
        }
    }

    public IReadOnlyList<ReviewFinding> ParseFindingsJsonArray(JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayElement.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray();
    }

    private static ReviewFinding? MapFinding(JsonElement element)
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

        return new ReviewFinding(
            file,
            ReadString(element, "line_hint") ?? ReadString(element, "location") ?? "Unknown",
            ParseCategory(ReadString(element, "type")),
            ParseSeverity(ReadString(element, "severity")),
            ReviewFindingSource.InitialReview,
            title,
            ReadString(element, "description") ?? ReadString(element, "problem") ?? string.Empty,
            ReadString(element, "existing_code") ?? ReadString(element, "bad_code") ?? string.Empty,
            ReadString(element, "suggestion") ?? ReadString(element, "fix") ?? string.Empty,
            ReadInt(element, "start_line"),
            ReadInt(element, "end_line"),
            Guid.NewGuid());
    }

    private static ReviewOpportunityItem? MapOpportunity(JsonElement element)
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

        var description = ReadString(element, "description")?.Trim() ?? string.Empty;
        var suggestion = ReadString(element, "suggestion")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(suggestion))
        {
            return null;
        }

        return new ReviewOpportunityItem(
            file.Trim(),
            ReadString(element, "line_hint")?.Trim() ?? string.Empty,
            title.Trim(),
            description,
            suggestion,
            ReadInt(element, "start_line"));
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

    private static string NormalizeJsonPayload(string raw)
    {
        var payload = raw.Trim();
        if (!payload.StartsWith("```", StringComparison.Ordinal))
        {
            return payload;
        }

        return payload.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
