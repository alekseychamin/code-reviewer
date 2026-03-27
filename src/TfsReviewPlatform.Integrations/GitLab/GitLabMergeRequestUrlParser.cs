namespace TfsReviewPlatform.Integrations.GitLab;

public static class GitLabMergeRequestUrlParser
{
    public static GitLabMergeRequestRef Parse(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mergeRequestIndex = Array.FindIndex(segments, value =>
            string.Equals(value, "merge_requests", StringComparison.OrdinalIgnoreCase));

        if (mergeRequestIndex < 0 || mergeRequestIndex + 1 >= segments.Length)
        {
            throw new InvalidOperationException("The pull request URL is not a recognized GitLab merge request URL.");
        }

        var projectSegmentCount =
            mergeRequestIndex > 0 &&
            string.Equals(segments[mergeRequestIndex - 1], "-", StringComparison.OrdinalIgnoreCase)
                ? mergeRequestIndex - 1
                : mergeRequestIndex;

        if (projectSegmentCount < 2)
        {
            throw new InvalidOperationException("The pull request URL is not a recognized GitLab merge request URL.");
        }

        var hostRoot = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        var projectPath = string.Join('/', segments.Take(projectSegmentCount));
        var mergeRequestIid = segments[mergeRequestIndex + 1];

        return new GitLabMergeRequestRef(
            hostRoot,
            $"{hostRoot}/api/v4",
            projectPath,
            segments[projectSegmentCount - 1],
            mergeRequestIid);
    }
}

public sealed record GitLabMergeRequestRef(
    string HostRoot,
    string ApiRoot,
    string ProjectPath,
    string ProjectName,
    string MergeRequestIid)
{
    public string EncodedProjectPath => Uri.EscapeDataString(ProjectPath);

    public string BuildMergeRequestApiUrl()
        => $"{ApiRoot}/projects/{EncodedProjectPath}/merge_requests/{Uri.EscapeDataString(MergeRequestIid)}";

    public string BuildNotesApiUrl()
        => $"{BuildMergeRequestApiUrl()}/notes";

    public string BuildDiscussionsApiUrl()
        => $"{BuildMergeRequestApiUrl()}/discussions";

    public string BuildVersionsApiUrl()
        => $"{BuildMergeRequestApiUrl()}/versions";

    public string BuildRemoteUrl()
        => $"{HostRoot}/{ProjectPath}.git";

    public string BuildMergeRequestHeadRef()
        => $"refs/merge-requests/{MergeRequestIid}/head";
}
