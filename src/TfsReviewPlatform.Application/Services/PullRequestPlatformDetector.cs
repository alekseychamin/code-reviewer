using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public static class PullRequestPlatformDetector
{
    public static PullRequestPlatformKind Detect(string? pullRequestUrl)
    {
        if (string.IsNullOrWhiteSpace(pullRequestUrl) ||
            !Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri))
        {
            return PullRequestPlatformKind.Unknown;
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 4 &&
            string.Equals(segments[2], "pull", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestPlatformKind.GitHub;
        }

        var mergeRequestIndex = Array.FindIndex(segments, value =>
            string.Equals(value, "merge_requests", StringComparison.OrdinalIgnoreCase));
        if (mergeRequestIndex >= 0)
        {
            var projectSegmentCount =
                mergeRequestIndex > 0 &&
                string.Equals(segments[mergeRequestIndex - 1], "-", StringComparison.OrdinalIgnoreCase)
                    ? mergeRequestIndex - 1
                    : mergeRequestIndex;

            if (projectSegmentCount >= 2 && mergeRequestIndex + 1 < segments.Length)
            {
                return PullRequestPlatformKind.GitLab;
            }
        }

        if (segments.Contains("_git", StringComparer.OrdinalIgnoreCase) &&
            segments.Contains("pullrequest", StringComparer.OrdinalIgnoreCase))
        {
            return PullRequestPlatformKind.AzureDevOps;
        }

        return PullRequestPlatformKind.Unknown;
    }

    public static string Normalize(string pullRequestUrl)
    {
        return Detect(pullRequestUrl) switch
        {
            PullRequestPlatformKind.AzureDevOps => NormalizeAzureDevOps(pullRequestUrl),
            PullRequestPlatformKind.GitHub => NormalizeGitHub(pullRequestUrl),
            PullRequestPlatformKind.GitLab => NormalizeGitLab(pullRequestUrl),
            _ => pullRequestUrl.Trim()
        };
    }

    private static string NormalizeAzureDevOps(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var gitIndex = Array.FindIndex(segments, value => string.Equals(value, "_git", StringComparison.OrdinalIgnoreCase));
        var prIndex = Array.FindIndex(segments, value => string.Equals(value, "pullrequest", StringComparison.OrdinalIgnoreCase));

        if (gitIndex < 0 || prIndex < 0 || gitIndex + 1 >= segments.Length || prIndex + 1 >= segments.Length)
        {
            return pullRequestUrl.Trim();
        }

        var hostRoot = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        var projectPath = string.Join('/', segments.Take(gitIndex));
        var repositoryName = segments[gitIndex + 1];
        var pullRequestId = segments[prIndex + 1];

        return $"{hostRoot}/{projectPath}/_git/{repositoryName}/pullrequest/{pullRequestId}";
    }

    private static string NormalizeGitHub(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 4)
        {
            return pullRequestUrl.Trim();
        }

        var hostRoot = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        return $"{hostRoot}/{segments[0]}/{segments[1]}/pull/{segments[3]}";
    }

    private static string NormalizeGitLab(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mergeRequestIndex = Array.FindIndex(segments, value =>
            string.Equals(value, "merge_requests", StringComparison.OrdinalIgnoreCase));

        if (mergeRequestIndex < 0 || mergeRequestIndex + 1 >= segments.Length)
        {
            return pullRequestUrl.Trim();
        }

        var projectSegmentCount =
            mergeRequestIndex > 0 &&
            string.Equals(segments[mergeRequestIndex - 1], "-", StringComparison.OrdinalIgnoreCase)
                ? mergeRequestIndex - 1
                : mergeRequestIndex;

        if (projectSegmentCount < 2)
        {
            return pullRequestUrl.Trim();
        }

        var hostRoot = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        var projectPath = string.Join('/', segments.Take(projectSegmentCount));
        var mergeRequestId = segments[mergeRequestIndex + 1];
        return $"{hostRoot}/{projectPath}/-/merge_requests/{mergeRequestId}";
    }
}
