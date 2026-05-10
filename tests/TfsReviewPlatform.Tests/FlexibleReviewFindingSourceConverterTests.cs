using System.Text.Json;
using System.Text.Json.Serialization;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Infrastructure.Persistence;

namespace TfsReviewPlatform.Tests;

public sealed class FlexibleReviewFindingSourceConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new FlexibleReviewFindingSourceConverter(),
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public void Deserialize_maps_legacy_DiffRiskHint_to_InitialReview()
    {
        var json = "\"DiffRiskHint\"";
        var source = JsonSerializer.Deserialize<ReviewFindingSource>(json, Options);
        Assert.Equal(ReviewFindingSource.InitialReview, source);
    }

    [Fact]
    public void Deserialize_still_accepts_known_values()
    {
        Assert.Equal(
            ReviewFindingSource.FollowUpDiscussion,
            JsonSerializer.Deserialize<ReviewFindingSource>("\"FollowUpDiscussion\"", Options));
    }
}
