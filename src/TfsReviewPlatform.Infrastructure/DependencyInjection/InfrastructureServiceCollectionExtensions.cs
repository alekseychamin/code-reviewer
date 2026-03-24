using Microsoft.Extensions.DependencyInjection;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Infrastructure.Persistence;
using TfsReviewPlatform.Infrastructure.Services;

namespace TfsReviewPlatform.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IReviewRunRepository, InMemoryReviewRunRepository>();
        services.AddSingleton<IReviewProgressStore, InMemoryReviewProgressStore>();
        services.AddSingleton<IProviderProfileRepository, InMemoryProviderProfileRepository>();
        services.AddSingleton<IBackgroundReviewScheduler, BackgroundReviewScheduler>();

        return services;
    }
}
