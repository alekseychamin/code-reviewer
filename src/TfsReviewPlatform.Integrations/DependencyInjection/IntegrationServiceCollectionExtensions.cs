using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Integrations.AzureDevOps;
using TfsReviewPlatform.Integrations.Git;
using TfsReviewPlatform.Integrations.GitHub;
using TfsReviewPlatform.Integrations.GitLab;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.Publishing;
using TfsReviewPlatform.Integrations.PullRequests;
using TfsReviewPlatform.Integrations.Prompts;
using TfsReviewPlatform.Integrations.Qdrant;

namespace TfsReviewPlatform.Integrations.DependencyInjection;

public static class IntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));

        services.AddHttpClient(HttpClientNames.AzureDevOps);
        services.AddHttpClient(HttpClientNames.GitHub, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tfs-review-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services.AddHttpClient(HttpClientNames.GitLab, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tfs-review-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddHttpClient(HttpClientNames.Qdrant, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tfs-review-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
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
        services.AddSingleton<IReviewWorkspaceToolExecutor, GitReviewWorkspaceToolExecutor>();
        services.AddSingleton<IBranchComparisonDiffService, BranchComparisonDiffService>();
        services.AddSingleton<IPullRequestDiffProvider, AzureDevOpsPullRequestDiffProvider>();
        services.AddSingleton<IPullRequestDiffProvider, GitHubPullRequestDiffProvider>();
        services.AddSingleton<IPullRequestDiffProvider, GitLabPullRequestDiffProvider>();
        services.AddSingleton<IPullRequestDiffService, PullRequestDiffService>();
        services.AddSingleton<ILlmCompletionService, LlmCompletionService>();
        services.AddSingleton<IReviewPromptFactory, ReviewPromptFactory>();
        services.AddSingleton<IReviewSemanticIndex, QdrantReviewSemanticIndex>();
        services.AddSingleton<IPullRequestReviewPublisherProvider, AzureDevOpsReviewPublisherProvider>();
        services.AddSingleton<IPullRequestReviewPublisherProvider, GitHubReviewPublisherProvider>();
        services.AddSingleton<IPullRequestReviewPublisherProvider, GitLabReviewPublisherProvider>();
        services.AddSingleton<IReviewPublisher, ReviewPublisher>();

        return services;
    }
}
