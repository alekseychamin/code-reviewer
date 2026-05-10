using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProviderCatalogOptions>(configuration.GetSection(ProviderCatalogOptions.SectionName));
        services.Configure<ReviewRoutingOptions>(configuration.GetSection(ReviewRoutingOptions.SectionName));
        services.Configure<ReviewPipelineOptions>(configuration.GetSection(ReviewPipelineOptions.SectionName));
        services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));

        services.AddSingleton<IReviewRequestValidator, ReviewRequestValidator>();
        services.AddSingleton<IDiffPreprocessor, DiffPreprocessor>();
        services.AddSingleton<IChunkReviewResponseParser, ChunkReviewResponseParser>();
        services.AddSingleton<IFindingsComparisonService, FindingsComparisonService>();
        services.AddSingleton<IMarkdownReportBuilder, MarkdownReportBuilder>();
        services.AddScoped<IReviewRunExecutor, ReviewRunExecutor>();
        services.AddScoped<IReviewOrchestrator, ReviewOrchestrator>();
        services.AddSingleton<ILlmStageRouter, LlmStageRouter>();
        services.AddScoped<IProviderCatalogService, ProviderCatalogService>();

        return services;
    }
}
