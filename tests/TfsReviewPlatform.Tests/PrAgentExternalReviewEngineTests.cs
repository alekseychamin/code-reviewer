using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;
using TfsReviewPlatform.Integrations.ExternalReview;

namespace TfsReviewPlatform.Tests;

public sealed class PrAgentExternalReviewEngineTests
{
    [Fact]
    public async Task RunAsync_DiffMode_SendsPreparedDiffPayload()
    {
        var handler = new CapturingHandler("""
            {
              "succeeded": true,
              "elapsed_ms": 42,
              "commands": [
                {
                  "command": "describe",
                  "succeeded": true,
                  "elapsed_ms": 7,
                  "artifact": "### Description\n- ok",
                  "error_message": ""
                }
              ]
            }
            """);
        using var client = new HttpClient(handler);
        var sut = new PrAgentExternalReviewEngine(
            new StaticHttpClientFactory(client),
            Options.Create(new ExternalReviewOptions
            {
                Enabled = true,
                BaseUrl = "http://pr-agent-sidecar",
                InputMode = ExternalReviewInputMode.Diff,
                Commands = ["describe"],
                ResponseLanguage = "ru-ru"
            }),
            NullLogger<PrAgentExternalReviewEngine>.Instance);
        var run = new ReviewRun(
            Guid.NewGuid(),
            new ReviewTargetDescriptor(
                ReviewTargetKind.PullRequest,
                "https://tfs.example/Collection/Project/_git/Repo/pullrequest/123",
                "https://tfs.example/Collection/Project/_git/Repo/pullrequest/123",
                null,
                null,
                null,
                null),
            null);
        run.UpdateMetadata("Repo", "Alice", "PR title");

        var result = await sut.RunAsync(
            run,
            new ExternalReviewInput
            {
                DiffText = "diff --git a/src/Foo.cs b/src/Foo.cs",
                ChangedFiles = ["src/Foo.cs"],
                RepositoryName = "Repo",
                ServiceName = "Repo",
                PullRequestTitle = "PR title",
                SourceRef = "refs/heads/feature",
                TargetRef = "refs/heads/main"
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("http://pr-agent-sidecar/api/run", handler.RequestUri?.ToString());
        using var document = JsonDocument.Parse(handler.RequestBody ?? string.Empty);
        var root = document.RootElement;
        Assert.Equal("diff", root.GetProperty("input_mode").GetString());
        Assert.Equal("diff --git a/src/Foo.cs b/src/Foo.cs", root.GetProperty("diff").GetString());
        Assert.Equal("https://tfs.example/Collection/Project/_git/Repo/pullrequest/123", root.GetProperty("pr_url").GetString());
        Assert.Equal("Repo", root.GetProperty("repository").GetString());
        Assert.Equal("PR title", root.GetProperty("title").GetString());
        Assert.Equal("src/Foo.cs", root.GetProperty("changed_files")[0].GetString());
        Assert.False(root.TryGetProperty("access_token", out _));
        Assert.False(root.TryGetProperty("pat", out _));
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
