namespace TfsReviewPlatform.Integrations.GitHub;

public static class GitHubPullRequestUrlParser
{
    public static GitHubPullRequestRef Parse(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 4 || !string.Equals(segments[2], "pull", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The pull request URL is not a recognized GitHub pull request URL.");
        }

        return new GitHubPullRequestRef(
            $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}",
            BuildApiRoot(uri),
            segments[0],
            segments[1],
            segments[3]);
    }

    private static string BuildApiRoot(Uri uri)
    {
        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.github.com";
        }

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}/api/v3";
    }
}

public sealed record GitHubPullRequestRef(
    string HostRoot,
    string ApiRoot,
    string Owner,
    string RepositoryName,
    string PullRequestNumber)
{
    public string BuildPullRequestApiUrl()
        => $"{ApiRoot}/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(RepositoryName)}/pulls/{Uri.EscapeDataString(PullRequestNumber)}";

    public string BuildIssueCommentsApiUrl()
        => $"{ApiRoot}/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(RepositoryName)}/issues/{Uri.EscapeDataString(PullRequestNumber)}/comments";

    public string BuildReviewCommentsApiUrl()
        => $"{ApiRoot}/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(RepositoryName)}/pulls/{Uri.EscapeDataString(PullRequestNumber)}/comments";

    public string BuildPullHeadRef()
        => $"refs/pull/{PullRequestNumber}/head";
}
