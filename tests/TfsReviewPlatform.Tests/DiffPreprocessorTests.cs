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
}
