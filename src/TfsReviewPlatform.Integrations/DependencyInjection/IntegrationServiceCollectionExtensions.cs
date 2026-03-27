using Microsoft.Extensions.DependencyInjection;
using System.Net;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Integrations.AzureDevOps;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.GitHub;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.Publishing;
using TfsReviewPlatform.Integrations.PullRequests;
using TfsReviewPlatform.Integrations.Prompts;

namespace TfsReviewPlatform.Integrations.DependencyInjection;

public static class IntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientNames.AzureDevOps);
        services.AddHttpClient(HttpClientNames.GitHub, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tfs-review-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services
            .AddHttpClient(HttpClientNames.OpenAiCompatibleLlm, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestVersion = HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            });

        services
            .AddHttpClient(HttpClientNames.OllamaLlm, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestVersion = HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            });

        services.AddSingleton<ShellGitCommandRunner>();
        services.AddSingleton<IBranchRepositoryLookupService, GitBranchRepositoryLookupService>();
        services.AddSingleton<IRepositoryFileContentService, GitRepositoryFileContentService>();
        services.AddSingleton<IBranchComparisonDiffService, BranchComparisonDiffService>();
        services.AddSingleton<IPullRequestDiffProvider, AzureDevOpsPullRequestDiffProvider>();
        services.AddSingleton<IPullRequestDiffProvider, GitHubPullRequestDiffProvider>();
        services.AddSingleton<IPullRequestDiffService, PullRequestDiffService>();
        services.AddSingleton<ILlmCompletionService, LlmCompletionService>();
        services.AddSingleton<IReviewPromptFactory, ReviewPromptFactory>();
        services.AddSingleton<IPullRequestReviewPublisherProvider, AzureDevOpsReviewPublisherProvider>();
        services.AddSingleton<IPullRequestReviewPublisherProvider, GitHubReviewPublisherProvider>();
        services.AddSingleton<IReviewPublisher, ReviewPublisher>();

        return services;
    }
}
