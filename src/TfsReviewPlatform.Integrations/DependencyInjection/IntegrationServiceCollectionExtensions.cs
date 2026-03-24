using Microsoft.Extensions.DependencyInjection;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Integrations.AzureDevOps;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.Prompts;

namespace TfsReviewPlatform.Integrations.DependencyInjection;

public static class IntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientNames.AzureDevOps);
        services.AddSingleton<ShellGitCommandRunner>();
        services.AddSingleton<IBranchComparisonDiffService, BranchComparisonDiffService>();
        services.AddSingleton<IPullRequestDiffService, PullRequestDiffService>();
        services.AddSingleton<ILlmCompletionService, LlmCompletionService>();
        services.AddSingleton<IReviewPromptFactory, ReviewPromptFactory>();
        services.AddSingleton<IReviewPublisher, AzureDevOpsReviewPublisher>();

        return services;
    }
}
