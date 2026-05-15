using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.PullRequests;

namespace TfsReviewPlatform.Integrations.GitHub;

internal sealed class GitHubPullRequestDiffProvider(
    IHttpClientFactory httpClientFactory,
    ShellGitCommandRunner gitCommandRunner,
    ILogger<GitHubPullRequestDiffProvider> logger)
    : IPullRequestDiffProvider
{
    public bool CanHandle(string pullRequestUrl)
    {
        try
        {
            GitHubPullRequestUrlParser.Parse(pullRequestUrl);
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
        var reference = GitHubPullRequestUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.GitHub);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await client.GetAsync(reference.BuildPullRequestApiUrl(), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub pull request metadata request failed with status {(int)response.StatusCode}: {TrimForLog(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var remoteUrl = root.GetProperty("base").GetProperty("repo").GetProperty("clone_url").GetString()
                        ?? throw new InvalidOperationException("GitHub response does not contain base.repo.clone_url.");
        var targetRef = root.GetProperty("base").GetProperty("ref").GetString()
                        ?? throw new InvalidOperationException("GitHub response does not contain base.ref.");
        var repositoryName = root.GetProperty("base").GetProperty("repo").GetProperty("name").GetString()
                             ?? reference.RepositoryName;
        var pullRequestTitle = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        var authorName = TryReadAuthor(root);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "tfs-review-platform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        await gitCommandRunner.RunAsync(tempDirectory, ["init"], cancellationToken);
        await gitCommandRunner.RunAsync(tempDirectory, ["remote", "add", "origin", remoteUrl], cancellationToken);

        string? httpExtraHeader = null;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            httpExtraHeader = $"Authorization: Basic {EncodeGitHubToken(accessToken)}";
            await gitCommandRunner.RunAsync(
                tempDirectory,
                ["config", "http.extraHeader", httpExtraHeader],
                cancellationToken);
        }

        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["fetch", "origin", $"{targetRef}:refs/remotes/origin/__target__", "--depth=100"],
            cancellationToken);
        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["fetch", "origin", $"{reference.BuildPullHeadRef()}:refs/remotes/origin/__source__", "--depth=100"],
            cancellationToken);

        var diffText = await gitCommandRunner.RunAsync(
            tempDirectory,
            ["diff", "origin/__target__...origin/__source__"],
            cancellationToken);
        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["checkout", "--detach", "origin/__source__"],
            cancellationToken);

        logger.LogInformation(
            "Fetched GitHub pull request diff for {Repository}#{PullRequestNumber}",
            repositoryName,
            reference.PullRequestNumber);

        return new DiffAcquisitionResult
        {
            DiffText = diffText,
            RepositoryPath = tempDirectory,
            RepositoryName = repositoryName,
            PullRequestTitle = pullRequestTitle,
            ServiceName = repositoryName,
            AuthorName = authorName,
            PullRequestUrl = pullRequestUrl,
            SourceRef = "origin/__source__",
            TargetRef = "origin/__target__",
            CleanupDirectory = tempDirectory,
            RepositoryRemoteUrl = remoteUrl,
            GitFetchSourceRef = reference.BuildPullHeadRef(),
            GitFetchTargetRef = targetRef,
            GitHttpExtraHeader = httpExtraHeader
        };
    }

    private static string EncodeGitHubToken(string accessToken)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{accessToken}"));

    private static string? TryReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var login = user.TryGetProperty("login", out var loginElement) &&
                    loginElement.ValueKind == JsonValueKind.String
            ? loginElement.GetString()
            : null;

        var displayName = user.TryGetProperty("name", out var nameElement) &&
                          nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        return (displayName, login) switch
        {
            ({ Length: > 0 } name, { Length: > 0 } userLogin) => $"{name} <{userLogin}>",
            (_, { Length: > 0 } userLogin) => userLogin,
            _ => null
        };
    }

    private static string TrimForLog(string text)
    {
        var compact = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length <= 400 ? compact : compact[..400];
    }
}
