using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.PullRequests;

namespace TfsReviewPlatform.Integrations.GitLab;

internal sealed class GitLabPullRequestDiffProvider(
    IHttpClientFactory httpClientFactory,
    ShellGitCommandRunner gitCommandRunner,
    ILogger<GitLabPullRequestDiffProvider> logger)
    : IPullRequestDiffProvider
{
    public bool CanHandle(string pullRequestUrl)
    {
        try
        {
            GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
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
        var reference = GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.GitLab);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", accessToken);
        }

        using var response = await client.GetAsync(reference.BuildMergeRequestApiUrl(), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitLab merge request metadata request failed with status {(int)response.StatusCode}: {TrimForLog(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var targetBranch = root.GetProperty("target_branch").GetString()
                           ?? throw new InvalidOperationException("GitLab response does not contain target_branch.");
        var pullRequestTitle = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        var authorName = TryReadAuthor(root);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "tfs-review-platform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        await gitCommandRunner.RunAsync(tempDirectory, ["init"], cancellationToken);
        await gitCommandRunner.RunAsync(tempDirectory, ["remote", "add", "origin", reference.BuildRemoteUrl()], cancellationToken);

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            await gitCommandRunner.RunAsync(
                tempDirectory,
                ["config", "http.extraHeader", $"Authorization: Basic {EncodeGitLabToken(accessToken)}"],
                cancellationToken);
        }

        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["fetch", "origin", $"{targetBranch}:refs/remotes/origin/__target__", "--depth=100"],
            cancellationToken);
        await gitCommandRunner.RunAsync(
            tempDirectory,
            ["fetch", "origin", $"{reference.BuildMergeRequestHeadRef()}:refs/remotes/origin/__source__", "--depth=100"],
            cancellationToken);

        var diffText = await gitCommandRunner.RunAsync(
            tempDirectory,
            ["diff", "origin/__target__...origin/__source__"],
            cancellationToken);

        logger.LogInformation(
            "Fetched GitLab merge request diff for {Repository}!{MergeRequestIid}",
            reference.ProjectName,
            reference.MergeRequestIid);

        return new DiffAcquisitionResult
        {
            DiffText = diffText,
            RepositoryPath = tempDirectory,
            RepositoryName = reference.ProjectName,
            PullRequestTitle = pullRequestTitle,
            ServiceName = reference.ProjectName,
            AuthorName = authorName,
            PullRequestUrl = pullRequestUrl,
            SourceRef = "origin/__source__",
            TargetRef = "origin/__target__",
            CleanupDirectory = tempDirectory
        };
    }

    private static string EncodeGitLabToken(string accessToken)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"oauth2:{accessToken}"));

    private static string? TryReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var username = author.TryGetProperty("username", out var usernameElement) &&
                       usernameElement.ValueKind == JsonValueKind.String
            ? usernameElement.GetString()
            : null;

        var name = author.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        return (name, username) switch
        {
            ({ Length: > 0 } displayName, { Length: > 0 } login) => $"{displayName} <{login}>",
            ({ Length: > 0 } displayName, _) => displayName,
            (_, { Length: > 0 } login) => login,
            _ => null
        };
    }

    private static string TrimForLog(string text)
    {
        var compact = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length <= 400 ? compact : compact[..400];
    }
}
