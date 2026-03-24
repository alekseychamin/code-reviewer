using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.AzureDevOps;

public sealed class PullRequestDiffService(
    IHttpClientFactory httpClientFactory,
    ShellGitCommandRunner gitCommandRunner,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    ILogger<PullRequestDiffService> logger)
    : IPullRequestDiffService
{
    private static readonly string[] ApiVersionsToTry = ["7.0", "6.0", "5.1", "4.1"];

    public async Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var reference = AzureDevOpsUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.AzureDevOps);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", EncodePat(accessToken));
        using var document = await GetPullRequestMetadataAsync(client, reference, cancellationToken);
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

    private async Task<JsonDocument> GetPullRequestMetadataAsync(
        HttpClient client,
        AzureDevOpsPullRequestRef reference,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var apiVersion in ApiVersionsToTry)
        {
            var url = reference.BuildPullRequestApiUrl(apiVersion);

            try
            {
                logger.LogInformation(
                    "Fetching PR metadata from Azure DevOps/TFS using api-version {ApiVersion} for repository {RepositoryName}",
                    apiVersion,
                    reference.RepositoryName);

                using var response = await client.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                failures.Add($"api-version={apiVersion}, status={(int)response.StatusCode}, body={TrimForLog(responseBody)}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"api-version={apiVersion}, exception={exception.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Could not load pull request metadata from Azure DevOps/TFS for '{reference.RepositoryName}' PR '{reference.PullRequestId}'. Tried versions: {string.Join(" | ", failures)}");
    }

    private static string EncodePat(string accessToken)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"pat:{accessToken}"));

    private static string TrimForLog(string text)
    {
        var compact = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length <= 400 ? compact : compact[..400];
    }
}
