using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class FindingsComparisonServiceTests
{
    [Fact]
    public void Compare_MarksSameFindingAsStillRelevant()
    {
        var sut = new FindingsComparisonService();
        var previous = new[]
        {
            CreateFinding(
                "src/App/Service.cs",
                "Possible null dereference",
                "return request.Value.Length;",
                FindingSeverity.High)
        };
        var current = new[]
        {
            CreateFinding(
                "src/App/Service.cs",
                "Possible null dereference",
                "return request.Value.Length;",
                FindingSeverity.High)
        };

        var comparison = sut.Compare(Guid.NewGuid(), previous, current);

        Assert.Equal(0, comparison.NewFindingsCount);
        Assert.Equal(1, comparison.StillRelevantFindingsCount);
        Assert.Equal(0, comparison.ResolvedFindingsCount);
    }

    [Fact]
    public void Compare_MarksRemovedFindingAsResolvedAndNewOneAsNew()
    {
        var sut = new FindingsComparisonService();
        var previous = new[]
        {
            CreateFinding(
                "src/App/Service.cs",
                "Possible null dereference",
                "return request.Value.Length;",
                FindingSeverity.High)
        };
        var current = new[]
        {
            CreateFinding(
                "src/App/Service.cs",
                "Potential timeout handling gap",
                "await client.SendAsync(request);",
                FindingSeverity.Medium)
        };

        var comparison = sut.Compare(Guid.NewGuid(), previous, current);

        Assert.Equal(1, comparison.NewFindingsCount);
        Assert.Equal(0, comparison.StillRelevantFindingsCount);
        Assert.Equal(1, comparison.ResolvedFindingsCount);
    }

    [Fact]
    public void Compare_UsesFuzzyMatchForRetitledFindingOnSameFile()
    {
        var sut = new FindingsComparisonService();
        var previous = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "Line 25",
                FindingCategory.Bug,
                FindingSeverity.High,
                "Missing null validation for request id",
                "The code uses request id before checking it.",
                "logger.LogInformation(cacheEntry.RequestId);",
                "Validate request id before logging it.")
        };
        var current = new[]
        {
            new ReviewFinding(
                "src/App/Service.cs",
                "Line 27",
                FindingCategory.Bug,
                FindingSeverity.High,
                "Request id is logged before guard clause",
                "Request id is still consumed before the null or empty check.",
                "logger.LogInformation(cacheEntry.RequestId);",
                "Move the guard clause before logging.")
        };

        var comparison = sut.Compare(Guid.NewGuid(), previous, current);

        Assert.Equal(0, comparison.NewFindingsCount);
        Assert.Equal(1, comparison.StillRelevantFindingsCount);
        Assert.Equal(0, comparison.ResolvedFindingsCount);
    }

    private static ReviewFinding CreateFinding(
        string file,
        string title,
        string existingCode,
        FindingSeverity severity)
    {
        return new ReviewFinding(
            file,
            "Line 10",
            FindingCategory.Bug,
            severity,
            title,
            $"{title} description.",
            existingCode,
            "Suggested fix");
    }
}
