using TfsReviewPlatform.Integrations.ExternalReview;

namespace TfsReviewPlatform.Tests;

public sealed class SocraticodeMcpPreflightServiceTests
{
    [Fact]
    public void IsIndexReadyStatus_ReturnsFalse_WhenFullIndexStillInProgress()
    {
        var status = """
            Project: /review-workspaces/repositories/service
            Collection: socraticode_codebase_test
            Status: green
            Indexed chunks: 74

            ⚠ Full index in progress
              Phase: generating embeddings (batch 2/24)
              Progress: 106/5309 chunks embedded (2%)
            """;

        Assert.False(SocraticodeMcpPreflightService.IsIndexReadyStatus(status));
        Assert.Equal(2, SocraticodeMcpPreflightService.TryExtractProgressPercent(status));
    }

    [Fact]
    public void IsIndexReadyStatus_ReturnsTrue_WhenStatusIsGreenWithoutProgress()
    {
        var status = """
            Project: /review-workspaces/repositories/service
            Collection: socraticode_codebase_test
            Status: green
            Indexed chunks: 5309

            Last operation: Incremental update — completed
            File watcher: inactive
            """;

        Assert.True(SocraticodeMcpPreflightService.IsIndexReadyStatus(status));
        Assert.Equal(5309, SocraticodeMcpPreflightService.TryExtractIndexedChunks(status));
    }

    [Fact]
    public void IsIndexReadyStatus_ReturnsFalse_WhenGreenStatusHasNoChunks()
    {
        var status = """
            Project: /review-workspaces/repositories/service
            Collection: codebase_test
            Status: green
            Indexed chunks: 0

            File watcher: inactive
            Code graph: not built
              Run codebase_graph_build to build it, or codebase_index to re-index.
            """;

        Assert.True(SocraticodeMcpPreflightService.HasEmptyIndexStatus(status));
        Assert.False(SocraticodeMcpPreflightService.IsIndexReadyStatus(status));
    }

    [Fact]
    public void IsMissingIndexStatus_DetectsNoIndex()
    {
        var status = """
            No index found for project: /review-workspaces/repositories/service
            Run codebase_index to create one.
            """;

        Assert.True(SocraticodeMcpPreflightService.IsMissingIndexStatus(status));
        Assert.False(SocraticodeMcpPreflightService.IsIndexReadyStatus(status));
    }

    [Fact]
    public void SplitArguments_PreservesQuotedSegments()
    {
        var arguments = """-y --loglevel=error "socraticode@latest" --flag='hello world'""";

        var parsed = SocraticodeMcpPreflightService.SplitArguments(arguments);

        Assert.Equal(
            new[] { "-y", "--loglevel=error", "socraticode@latest", "--flag=hello world" },
            parsed);
    }
}
