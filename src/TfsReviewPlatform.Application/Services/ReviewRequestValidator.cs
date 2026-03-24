using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class ReviewRequestValidator : IReviewRequestValidator
{
    public IReadOnlyDictionary<string, string[]> Validate(StartPullRequestReviewRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.PublishMode != PublishMode.None && string.IsNullOrWhiteSpace(request.AzureDevOpsAccessToken))
        {
            errors["azureDevOpsAccessToken"] = ["Publishing requires an Azure DevOps/TFS access token."];
        }

        if (request.LocalOnlyMode && !string.IsNullOrWhiteSpace(request.ProviderProfileId) &&
            !request.ProviderProfileId.Contains("ollama", StringComparison.OrdinalIgnoreCase))
        {
            errors["providerProfileId"] = ["Local-only mode must use an Ollama-compatible provider profile."];
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
}
