using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Infrastructure.Persistence;
using TfsReviewPlatform.Infrastructure.Services;

namespace TfsReviewPlatform.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var reviewHistoryConnectionString = configuration.GetConnectionString("ReviewHistory");

        if (string.IsNullOrWhiteSpace(reviewHistoryConnectionString))
        {
            services.AddSingleton<IReviewRunRepository, InMemoryReviewRunRepository>();
        }
        else
        {
            services.AddSingleton<IReviewRunRepository>(_ => new PostgresReviewRunRepository(reviewHistoryConnectionString));
        }

        services.AddSingleton<IReviewProgressStore, InMemoryReviewProgressStore>();
        services.AddSingleton<IProviderProfileRepository, InMemoryProviderProfileRepository>();
        services.AddSingleton<IBackgroundReviewScheduler, BackgroundReviewScheduler>();

        return services;
    }
}
