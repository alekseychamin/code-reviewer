using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Tests;

public sealed class DiffToolFallbackContextBuilderTests
{
    [Fact]
    public void Build_ReturnsReadFileExcerpt_FromAddedFileDiff()
    {
        var diff = """
            diff --git a/src/App/NewService.cs b/src/App/NewService.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/App/NewService.cs
            @@ -0,0 +1,8 @@
            +namespace App;
            +
            +public sealed class NewService
            +{
            +    public string GetValue()
            +        => "ok";
            +}
            +
            """;
        var requests = new[]
        {
            new ReviewWorkspaceToolRequest(
                "read_file",
                "Need the new service body.",
                FilePath: "src/App/NewService.cs",
                StartLine: 3,
                MaxLines: 4)
        };

        var responses = DiffToolFallbackContextBuilder.Build(diff, requests, []);

        var response = Assert.Single(responses);
        Assert.Equal("read_file", response.ToolName);
        Assert.Equal("diff-fallback", response.Source);
        Assert.Equal("src/App/NewService.cs", response.FilePath);
        Assert.Equal(3, response.StartLine);
        Assert.Contains("public sealed class NewService", response.Content);
        Assert.Contains("GetValue", response.Content);
    }

    [Fact]
    public void Build_ReturnsGrepHits_FromChangedDiff()
    {
        var diff = """
            diff --git a/src/App/Options.cs b/src/App/Options.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/App/Options.cs
            @@ -0,0 +1,7 @@
            +namespace App;
            +
            +public sealed class Options
            +{
            +    public string IssuerSigningKey { get; init; } = "";
            +}
            """;
        var requests = new[]
        {
            new ReviewWorkspaceToolRequest(
                "grep_code",
                "Find config key.",
                Query: "IssuerSigningKey")
        };

        var responses = DiffToolFallbackContextBuilder.Build(diff, requests, []);

        var response = Assert.Single(responses);
        Assert.Equal("grep_code", response.ToolName);
        Assert.Equal("diff-fallback", response.Source);
        Assert.Equal("src/App/Options.cs", response.FilePath);
        Assert.Contains("IssuerSigningKey", response.Content);
        Assert.Contains("src/App/Options.cs:5", response.Content);
    }

    [Fact]
    public void Build_ReturnsFindFiles_FromPathQuery()
    {
        var diff = """
            diff --git a/src/App/FeatureHandler.cs b/src/App/FeatureHandler.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/App/FeatureHandler.cs
            @@ -0,0 +1,3 @@
            +namespace App;
            +public sealed class FeatureHandler;
            +
            """;
        var requests = new[]
        {
            new ReviewWorkspaceToolRequest(
                "find_files",
                "Find handler file.",
                Query: "FeatureHandler")
        };

        var responses = DiffToolFallbackContextBuilder.Build(diff, requests, []);

        var response = Assert.Single(responses);
        Assert.Equal("find_files", response.ToolName);
        Assert.Equal("diff-fallback", response.Source);
        Assert.Contains("- src/App/FeatureHandler.cs", response.Content);
    }
}
