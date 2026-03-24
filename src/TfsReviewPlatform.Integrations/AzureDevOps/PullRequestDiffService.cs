using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.AzureDevOps;

public sealed class PullRequestDiffService(
    IHttpClientFactory httpClientFactory,
    ShellGitCommandRunner gitCommandRunner,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions)
    : IPullRequestDiffService
{
    public async Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var reference = AzureDevOpsUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.AzureDevOps);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", EncodePat(accessToken));

        using var response = await client.GetAsync(reference.BuildPullRequestApiUrl(), cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var remoteUrl = root.GetProperty("repository").GetProperty("remoteUrl").GetString()
                        ?? throw new InvalidOperationException("Azure DevOps response does not contain a repository remoteUrl.");
        var sourceRef = root.GetProperty("sourceRefName").GetString()
                        ?? throw new InvalidOperationException("Azure DevOps response does not contain sourceRefName.");
        var targetRef = root.GetProperty("targetRefName").GetString()
                        ?? throw new InvalidOperationException("Azure DevOps response does not contain targetRefName.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "tfs-review-platform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await gitCommandRunner.RunAsync(tempDirectory, ["init"], cancellationToken);
            await gitCommandRunner.RunAsync(tempDirectory, ["remote", "add", "origin", remoteUrl], cancellationToken);
            await gitCommandRunner.RunAsync(
                tempDirectory,
                ["config", "http.extraHeader", $"Authorization: Basic {EncodePat(accessToken)}"],
                cancellationToken);

            if (azureDevOpsOptions.Value.SkipCertificateValidation)
            {
                await gitCommandRunner.RunAsync(tempDirectory, ["config", "http.sslVerify", "false"], cancellationToken);
            }

            await gitCommandRunner.RunAsync(
                tempDirectory,
                ["fetch", "origin", $"{targetRef}:refs/remotes/origin/__target__", "--depth=100"],
                cancellationToken);
            await gitCommandRunner.RunAsync(
                tempDirectory,
                ["fetch", "origin", $"{sourceRef}:refs/remotes/origin/__source__", "--depth=100"],
                cancellationToken);

            var diffText = await gitCommandRunner.RunAsync(
                tempDirectory,
                ["diff", "origin/__target__...origin/__source__"],
                cancellationToken);

            return new DiffAcquisitionResult
            {
                DiffText = diffText,
                RepositoryName = reference.RepositoryName,
                PullRequestUrl = pullRequestUrl,
                SourceRef = sourceRef,
                TargetRef = targetRef
            };
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    private static string EncodePat(string accessToken)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"pat:{accessToken}"));
}
