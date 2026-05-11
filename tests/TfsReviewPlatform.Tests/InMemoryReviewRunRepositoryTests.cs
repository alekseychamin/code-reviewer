using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;
using TfsReviewPlatform.Infrastructure.Persistence;

namespace TfsReviewPlatform.Tests;

public sealed class InMemoryReviewRunRepositoryTests
{
    [Fact]
    public async Task SearchByServiceAsync_ReturnsRunsByServiceSubstringNewestFirst()
    {
        var repository = new InMemoryReviewRunRepository();
        var olderRun = CreateRun(
            serviceName: "Tele2.Crm.BroadbandConnectionOrder",
            repositoryName: "tele2_crm_broadbandconnectionorder",
            title: "Older broadband PR",
            pullRequestUrl: "https://gitlab.local/group/broadband/-/merge_requests/1",
            createdAt: DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
        var newerRun = CreateRun(
            serviceName: "Tele2.Crm.BroadbandConnectionOrder",
            repositoryName: "tele2_crm_broadbandconnectionorder",
            title: "Newer broadband PR",
            pullRequestUrl: "https://gitlab.local/group/broadband/-/merge_requests/2",
            createdAt: DateTimeOffset.Parse("2026-05-11T10:00:00Z"));
        var unrelatedRun = CreateRun(
            serviceName: "Tele2.Crm.MarkerRepresentService",
            repositoryName: "markerrepresentservice",
            title: "Marker PR",
            pullRequestUrl: "https://gitlab.local/group/marker/-/merge_requests/3",
            createdAt: DateTimeOffset.Parse("2026-05-11T11:00:00Z"));

        await repository.AddAsync(olderRun, CancellationToken.None);
        await repository.AddAsync(newerRun, CancellationToken.None);
        await repository.AddAsync(unrelatedRun, CancellationToken.None);

        var results = await repository.SearchByServiceAsync("broad", 10, CancellationToken.None);

        Assert.Collection(
            results,
            item => Assert.Equal(newerRun.Id, item.Id),
            item => Assert.Equal(olderRun.Id, item.Id));
    }

    [Fact]
    public async Task SearchByServiceAsync_MatchesRepositoryTitleUrlAndBranchFields()
    {
        var repository = new InMemoryReviewRunRepository();
        var branchRun = CreateBranchRun(
            repositoryName: "T2_Casper_TipsAvailabilityService",
            sourceBranch: "feature/casper-history",
            targetBranch: "master",
            createdAt: DateTimeOffset.Parse("2026-05-11T12:00:00Z"));
        var titleRun = CreateRun(
            serviceName: "unknown",
            repositoryName: "unrelated",
            title: "Add service history search",
            pullRequestUrl: "https://gitlab.local/group/unrelated/-/merge_requests/9",
            createdAt: DateTimeOffset.Parse("2026-05-11T13:00:00Z"));

        await repository.AddAsync(branchRun, CancellationToken.None);
        await repository.AddAsync(titleRun, CancellationToken.None);

        var repositoryResults = await repository.SearchByServiceAsync("casper", 10, CancellationToken.None);
        var titleResults = await repository.SearchByServiceAsync("history search", 10, CancellationToken.None);

        Assert.Collection(repositoryResults, item => Assert.Equal(branchRun.Id, item.Id));
        Assert.Collection(titleResults, item => Assert.Equal(titleRun.Id, item.Id));
    }

    private static ReviewRun CreateRun(
        string serviceName,
        string repositoryName,
        string title,
        string pullRequestUrl,
        DateTimeOffset createdAt)
    {
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.PullRequest,
            pullRequestUrl,
            pullRequestUrl,
            null,
            repositoryName,
            null,
            null);

        return ReviewRun.Restore(
            Guid.NewGuid(),
            target,
            null,
            ReviewRunStatus.Completed,
            null,
            100,
            "Review completed",
            null,
            createdAt,
            createdAt,
            [],
            new ReviewArtifacts(),
            false,
            serviceName,
            null,
            title);
    }

    private static ReviewRun CreateBranchRun(
        string repositoryName,
        string sourceBranch,
        string targetBranch,
        DateTimeOffset createdAt)
    {
        var target = new ReviewTargetDescriptor(
            ReviewTargetKind.BranchComparison,
            $"{repositoryName}: {sourceBranch} -> {targetBranch}",
            null,
            $"/repositories/{repositoryName}",
            repositoryName,
            sourceBranch,
            targetBranch);

        return ReviewRun.Restore(
            Guid.NewGuid(),
            target,
            null,
            ReviewRunStatus.Completed,
            null,
            100,
            "Review completed",
            null,
            createdAt,
            createdAt,
            [],
            new ReviewArtifacts(),
            false,
            repositoryName,
            null,
            null);
    }
}
