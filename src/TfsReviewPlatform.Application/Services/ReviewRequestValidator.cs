using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRequestValidator : IReviewRequestValidator
{
    public IReadOnlyDictionary<string, string[]> Validate(StartPullRequestReviewRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var platform = PullRequestPlatformDetector.Detect(request.PullRequestUrl);
        var resolvedToken = ResolvePullRequestAccessToken(request.PullRequestUrl, request.AccessToken ?? request.AzureDevOpsAccessToken);

        if (platform == PullRequestPlatformKind.Unknown)
        {
            errors["pullRequestUrl"] =
            [
                "Поддерживаются только pull request URL из Azure DevOps/TFS и GitHub."
            ];
        }

        if (platform == PullRequestPlatformKind.AzureDevOps && string.IsNullOrWhiteSpace(resolvedToken))
        {
            errors["accessToken"] =
            [
                "Для pull request из Azure DevOps/TFS требуется токен в запросе или переменная окружения AZURE_DEVOPS_TOKEN."
            ];
        }

        if (request.PublishMode != PublishMode.None &&
            (platform == PullRequestPlatformKind.AzureDevOps || platform == PullRequestPlatformKind.GitHub) &&
            string.IsNullOrWhiteSpace(resolvedToken))
        {
            errors["accessToken"] =
            [
                platform == PullRequestPlatformKind.GitHub
                    ? "Публикация в GitHub требует токен в запросе или переменную окружения GITHUB_TOKEN."
                    : "Публикация в Azure DevOps/TFS требует токен в запросе или переменную окружения AZURE_DEVOPS_TOKEN."
            ];
        }

        return errors;
    }

    public IReadOnlyDictionary<string, string[]> Validate(StartBranchReviewRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.PublishMode != PublishMode.None)
        {
            errors["publishMode"] = ["Branch comparison reviews cannot publish directly to a pull request."];
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            errors["repositoryName"] = ["Укажи название репозитория для сравнения веток."];
        }
        else if (request.RepositoryName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
                 request.RepositoryName.Contains("..", StringComparison.Ordinal))
        {
            errors["repositoryName"] = ["Название репозитория должно быть именем папки без вложенных путей."];
        }

        return errors;
    }

    private static string? ResolvePullRequestAccessToken(string? pullRequestUrl, string? requestToken)
    {
        return !string.IsNullOrWhiteSpace(requestToken)
            ? requestToken
            : PullRequestPlatformDetector.Detect(pullRequestUrl) switch
            {
                PullRequestPlatformKind.AzureDevOps => Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN"),
                PullRequestPlatformKind.GitHub => Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                _ => null
            };
    }
}
