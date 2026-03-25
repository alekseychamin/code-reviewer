using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Tests;

public sealed class DiffPreprocessorTests
{
    [Fact]
    public void Process_FiltersIgnoredExtensions_AndBuildsChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 80
        }));

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            +++ b/src/App/Service.cs
            @@ -1,1 +1,3 @@
            +public class Service {}
            diff --git a/src/App/logo.png b/src/App/logo.png
            +++ b/src/App/logo.png
            @@ -0,0 +1 @@
            +binary
            diff --git a/tests/App/ServiceTests.cs b/tests/App/ServiceTests.cs
            +++ b/tests/App/ServiceTests.cs
            @@ -1,1 +1,3 @@
            +public class ServiceTests {}
            """;

        var result = sut.Process(diff);

        Assert.Contains("src/App/Service.cs", result.ChangedFiles);
        Assert.Contains("tests/App/ServiceTests.cs", result.ChangedFiles);
        Assert.DoesNotContain("src/App/logo.png", result.ChangedFiles);
        Assert.NotEmpty(result.Chunks);
    }

    [Fact]
    public void Process_SplitsOversizedSingleFileDiffIntoMultipleChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 120
        }));

        var diff = """
            diff --git a/src/App/HugeFile.cs b/src/App/HugeFile.cs
            +++ b/src/App/HugeFile.cs
            @@ -1,1 +1,6 @@
            +line 01 1234567890
            +line 02 1234567890
            +line 03 1234567890
            +line 04 1234567890
            +line 05 1234567890
            @@ -10,1 +15,6 @@
            +line 06 1234567890
            +line 07 1234567890
            +line 08 1234567890
            +line 09 1234567890
            +line 10 1234567890
            """;

        var result = sut.Process(diff);

        Assert.True(result.Chunks.Count >= 2);
        Assert.All(result.Chunks, chunk => Assert.True(chunk.Length <= 160));
    }

    [Fact]
    public void Process_KeepsDeletionOnlyHunksInArtifacts_ButRemovesThemFromReviewContext()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 1000
        }));

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            --- a/src/App/Service.cs
            +++ b/src/App/Service.cs
            @@ -1,3 +1,0 @@
            -old line 1
            -old line 2
            -old line 3
            @@ -10,2 +7,3 @@
             context
            +new line 1
            +new line 2
            """;

        var result = sut.Process(diff);

        Assert.Contains("-old line 1", result.FilteredDiffText);
        Assert.DoesNotContain("-old line 1", result.ReviewContextDiffText);
        Assert.Contains("+new line 1", result.ReviewContextDiffText);
        Assert.Single(result.Chunks);
    }

}
