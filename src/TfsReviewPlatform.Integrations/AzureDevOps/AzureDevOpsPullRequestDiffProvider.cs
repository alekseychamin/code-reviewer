using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.PullRequests;

namespace TfsReviewPlatform.Integrations.AzureDevOps;

internal sealed class AzureDevOpsPullRequestDiffProvider(
    IHttpClientFactory httpClientFactory,
    ShellGitCommandRunner gitCommandRunner,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    ILogger<AzureDevOpsPullRequestDiffProvider> logger)
    : IPullRequestDiffProvider
{
    private static readonly string[] ApiVersionsToTry = ["7.0", "6.0", "5.1", "4.1"];

    public bool CanHandle(string pullRequestUrl)
    {
        try
        {
            AzureDevOpsUrlParser.Parse(pullRequestUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DiffAcquisitionResult> GetDiffAsync(
        string pullRequestUrl,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Для pull request из Azure DevOps/TFS требуется токен в запросе или переменная окружения AZURE_DEVOPS_TOKEN.");
        }

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
        var pullRequestTitle = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        var authorName = TryReadAuthor(root);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "tfs-review-platform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        await gitCommandRunner.RunAsync(tempDirectory, ["init"], cancellationToken);
        await gitCommandRunner.RunAsync(tempDirectory, ["remote", "add", "origin", remoteUrl], cancellationToken);
        var httpExtraHeader = $"Authorization: Basic {EncodePat(accessToken)}";
        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["config", "http.extraHeader", httpExtraHeader],
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
        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["checkout", "--detach", "origin/__source__"],
            cancellationToken);

        return new DiffAcquisitionResult
        {
            DiffText = diffText,
            RepositoryPath = tempDirectory,
            RepositoryName = reference.RepositoryName,
            PullRequestTitle = pullRequestTitle,
            ServiceName = reference.RepositoryName,
            AuthorName = authorName,
            PullRequestUrl = pullRequestUrl,
            SourceRef = "origin/__source__",
            TargetRef = "origin/__target__",
            CleanupDirectory = tempDirectory,
            RepositoryRemoteUrl = remoteUrl,
            GitFetchSourceRef = sourceRef,
            GitFetchTargetRef = targetRef,
            GitHttpExtraHeader = httpExtraHeader
        };
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

    private static string? TryReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("createdBy", out var createdBy) || createdBy.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var displayName = createdBy.TryGetProperty("displayName", out var displayNameElement) &&
                          displayNameElement.ValueKind == JsonValueKind.String
            ? displayNameElement.GetString()
            : null;

        var uniqueName = createdBy.TryGetProperty("uniqueName", out var uniqueNameElement) &&
                         uniqueNameElement.ValueKind == JsonValueKind.String
            ? uniqueNameElement.GetString()
            : null;

        return (displayName, uniqueName) switch
        {
            ({ Length: > 0 } name, { Length: > 0 } login) => $"{name} <{login}>",
            ({ Length: > 0 } name, _) => name,
            (_, { Length: > 0 } login) => login,
            _ => null
        };
    }
}
