namespace TfsReviewPlatform.Integrations.AzureDevOps;

public static class AzureDevOpsUrlParser
{
    public static AzureDevOpsPullRequestRef Parse(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (!segments.Contains("_git", StringComparer.OrdinalIgnoreCase) ||
            !segments.Contains("pullrequest", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The pull request URL is not a recognized Azure DevOps/TFS pull request URL.");
        }

        var gitIndex = Array.FindIndex(segments, value => string.Equals(value, "_git", StringComparison.OrdinalIgnoreCase));
        var prIndex = Array.FindIndex(segments, value => string.Equals(value, "pullrequest", StringComparison.OrdinalIgnoreCase));

        return new AzureDevOpsPullRequestRef(
            $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}",
            string.Join('/', segments.Take(gitIndex)),
            segments[gitIndex + 1],
            segments[prIndex + 1]);
    }
}

public sealed record AzureDevOpsPullRequestRef(
    string HostRoot,
    string ProjectPath,
    string RepositoryName,
    string PullRequestId)
{
    public string BuildPullRequestApiUrl()
        => $"{HostRoot}/{ProjectPath}/_apis/git/repositories/{RepositoryName}/pullRequests/{PullRequestId}?api-version=7.0";

    public string BuildThreadsApiUrl()
        => $"{HostRoot}/{ProjectPath}/_apis/git/repositories/{RepositoryName}/pullRequests/{PullRequestId}/threads?api-version=7.0";
}
