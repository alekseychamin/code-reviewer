using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRequestValidator : IReviewRequestValidator
{
    public IReadOnlyDictionary<string, string[]> Validate(StartPullRequestReviewRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var resolvedToken = ResolveAzureDevOpsToken(request.AzureDevOpsAccessToken);

        if (request.PublishMode != PublishMode.None && string.IsNullOrWhiteSpace(resolvedToken))
        {
            errors["azureDevOpsAccessToken"] = ["Publishing requires an Azure DevOps/TFS access token in the request or AZURE_DEVOPS_TOKEN environment variable."];
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

        if (!Path.IsPathRooted(request.RepositoryPath))
        {
            errors["repositoryPath"] = ["Repository path must be absolute."];
        }

        return errors;
    }

    private static string? ResolveAzureDevOpsToken(string? requestToken)
    {
        return !string.IsNullOrWhiteSpace(requestToken)
            ? requestToken
            : Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
    }
}
