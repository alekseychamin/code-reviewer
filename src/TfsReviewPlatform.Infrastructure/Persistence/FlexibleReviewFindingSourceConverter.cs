using System.Text.Json;
using System.Text.Json.Serialization;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Infrastructure.Persistence;

/// <summary>
/// Deserializes <see cref="ReviewFindingSource"/> from Postgres snapshots; maps removed enum names (e.g. DiffRiskHint) to <see cref="ReviewFindingSource.InitialReview"/>.
/// </summary>
public sealed class FlexibleReviewFindingSourceConverter : JsonConverter<ReviewFindingSource>
{
    public override ReviewFindingSource Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return ReviewFindingSource.InitialReview;
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return ReviewFindingSource.InitialReview;
                }

                if (Enum.TryParse<ReviewFindingSource>(s, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }

                if (string.Equals(s, "DiffRiskHint", StringComparison.OrdinalIgnoreCase))
                {
                    return ReviewFindingSource.InitialReview;
                }

                return ReviewFindingSource.InitialReview;
            }
            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                return Enum.IsDefined(typeof(ReviewFindingSource), number)
                    ? (ReviewFindingSource)number
                    : ReviewFindingSource.InitialReview;
            default:
                throw new JsonException($"Cannot convert token {reader.TokenType} to ReviewFindingSource.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ReviewFindingSource value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
