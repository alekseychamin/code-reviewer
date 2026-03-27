using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Integrations.GitHub;
using TfsReviewPlatform.Integrations.GitLab;

namespace TfsReviewPlatform.Tests;

public sealed class PullRequestPlatformDetectorTests
{
    [Fact]
    public void Detect_RecognizesAzureDevOpsPullRequestUrl()
    {
        var platform = PullRequestPlatformDetector.Detect(
            "https://tfs.example.local/tfs/Main/Tele2/_git/MyRepo/pullrequest/42");

        Assert.Equal(PullRequestPlatformKind.AzureDevOps, platform);
    }

    [Fact]
    public void Detect_RecognizesGitHubPullRequestUrl()
    {
        var platform = PullRequestPlatformDetector.Detect(
            "https://github.com/openai/example-repo/pull/42");

        Assert.Equal(PullRequestPlatformKind.GitHub, platform);
    }

    [Fact]
    public void Detect_RecognizesGitLabMergeRequestUrl()
    {
        var platform = PullRequestPlatformDetector.Detect(
            "https://gitlab.example.com/group/subgroup/example-repo/-/merge_requests/42");

        Assert.Equal(PullRequestPlatformKind.GitLab, platform);
    }

    [Fact]
    public void GitHubParser_ParsesStandardPullRequestUrl()
    {
        var reference = GitHubPullRequestUrlParser.Parse("https://github.com/openai/example-repo/pull/42/files");

        Assert.Equal("openai", reference.Owner);
        Assert.Equal("example-repo", reference.RepositoryName);
        Assert.Equal("42", reference.PullRequestNumber);
        Assert.Equal("https://api.github.com/repos/openai/example-repo/pulls/42", reference.BuildPullRequestApiUrl());
        Assert.Equal("refs/pull/42/head", reference.BuildPullHeadRef());
    }

    [Fact]
    public void GitLabParser_ParsesStandardMergeRequestUrl()
    {
        var reference = GitLabMergeRequestUrlParser.Parse(
            "https://gitlab.example.com/group/subgroup/example-repo/-/merge_requests/42/commits");

        Assert.Equal("group/subgroup/example-repo", reference.ProjectPath);
        Assert.Equal("example-repo", reference.ProjectName);
        Assert.Equal("42", reference.MergeRequestIid);
        Assert.Equal(
            "https://gitlab.example.com/api/v4/projects/group%2Fsubgroup%2Fexample-repo/merge_requests/42",
            reference.BuildMergeRequestApiUrl());
        Assert.Equal("refs/merge-requests/42/head", reference.BuildMergeRequestHeadRef());
    }
}
