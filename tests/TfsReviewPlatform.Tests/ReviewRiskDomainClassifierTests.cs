using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Tests;

public sealed class ReviewRiskDomainClassifierTests
{
    [Fact]
    public void Classify_DetectsMajorRiskDomains_FromPathAndContentSignals()
    {
        var files = new (string FilePath, string Content)[]
        {
            ("src/Auth/TokenService.cs", """
                diff --git a/src/Auth/TokenService.cs b/src/Auth/TokenService.cs
                +++ b/src/Auth/TokenService.cs
                @@ -1,1 +1,5 @@
                +var jwt = new JwtSecurityToken(token);
                +return jwt.ValidTo < DateTime.Now.AddMinutes(1);
                +services.AddAuthentication().AddJwtBearer();
                """),
            ("src/Api/DependencyInjection/AddConfigs.cs", """
                diff --git a/src/Api/DependencyInjection/AddConfigs.cs b/src/Api/DependencyInjection/AddConfigs.cs
                +++ b/src/Api/DependencyInjection/AddConfigs.cs
                @@ -1,1 +1,5 @@
                +services.AddScoped<IClient, Client>();
                +services.Configure<ClientOptions>(configuration.GetRequiredSection("Client"));
                """),
            ("src/Integrations/BillingClient.cs", """
                diff --git a/src/Integrations/BillingClient.cs b/src/Integrations/BillingClient.cs
                +++ b/src/Integrations/BillingClient.cs
                @@ -1,1 +1,5 @@
                +var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
                +response.EnsureSuccessStatusCode();
                """),
            ("src/Cache/RegionCache.cs", """
                diff --git a/src/Cache/RegionCache.cs b/src/Cache/RegionCache.cs
                +++ b/src/Cache/RegionCache.cs
                @@ -1,1 +1,5 @@
                +var cacheKey = $"{tenantId}:{userId}:{systemId}";
                +_memoryCache.Set(cacheKey, value, TimeSpan.FromMinutes(5));
                """),
            ("src/Infrastructure/Db/GetOrders.sql", """
                diff --git a/src/Infrastructure/Db/GetOrders.sql b/src/Infrastructure/Db/GetOrders.sql
                +++ b/src/Infrastructure/Db/GetOrders.sql
                @@ -1,1 +1,5 @@
                +select * from orders o
                +left join regions r on r.id = o.region_id
                +where o.is_active = true
                """),
            ("src/Kafka/OrderConsumer.cs", """
                diff --git a/src/Kafka/OrderConsumer.cs b/src/Kafka/OrderConsumer.cs
                +++ b/src/Kafka/OrderConsumer.cs
                @@ -1,1 +1,5 @@
                +await _producer.Publish(topic, message, cancellationToken);
                +await consumer.Commit(offset);
                """),
            ("src/Controllers/OrdersController.cs", """
                diff --git a/src/Controllers/OrdersController.cs b/src/Controllers/OrdersController.cs
                +++ b/src/Controllers/OrdersController.cs
                @@ -1,1 +1,5 @@
                +[HttpGet]
                +public ActionResult<OrderResponse> Get([FromQuery] GetOrdersRequest request) => Ok();
                """),
            ("tests/OrdersControllerTests.cs", """
                diff --git a/tests/OrdersControllerTests.cs b/tests/OrdersControllerTests.cs
                +++ b/tests/OrdersControllerTests.cs
                @@ -1,1 +1,5 @@
                +[Fact]
                +public async Task Get_returns_seed_order() => response.Should().NotBeNull();
                """),
            ("src/Jobs/SyncWorker.cs", """
                diff --git a/src/Jobs/SyncWorker.cs b/src/Jobs/SyncWorker.cs
                +++ b/src/Jobs/SyncWorker.cs
                @@ -1,1 +1,5 @@
                +protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                +    => await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                +using var scope = _scopeFactory.CreateScope();
                +await Task.WhenAll(handlers.Select(handler => handler.RunAsync(stoppingToken)));
                """),
            ("src/Diagnostics/WorkerHealthCheck.cs", """
                diff --git a/src/Diagnostics/WorkerHealthCheck.cs b/src/Diagnostics/WorkerHealthCheck.cs
                +++ b/src/Diagnostics/WorkerHealthCheck.cs
                @@ -1,1 +1,7 @@
                +logger.LogInformation("Processed token {token} for email {email}", token, email);
                +builder.Services.AddHealthChecks().AddCheck<WorkerHealthCheck>("worker");
                +private static readonly Meter Meter = new("DemoService.Worker");
                +private static readonly ActivitySource ActivitySource = new("DemoService.Worker");
                """),
            ("src/Mapping/OrderProfile.cs", """
                diff --git a/src/Mapping/OrderProfile.cs b/src/Mapping/OrderProfile.cs
                +++ b/src/Mapping/OrderProfile.cs
                @@ -1,1 +1,5 @@
                +CreateMap<Order, OrderDto>().ForMember(x => x.Name, x => x.MapFrom(y => y.Title));
                +[JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
                +[JsonConverter(typeof(JsonStringEnumConverter))] public OrderStatus Status { get; init; }
                """),
            ("frontend/src/components/ReviewForm.tsx", """
                diff --git a/frontend/src/components/ReviewForm.tsx b/frontend/src/components/ReviewForm.tsx
                +++ b/frontend/src/components/ReviewForm.tsx
                @@ -1,1 +1,5 @@
                +const [loading, setLoading] = useState(false);
                +useEffect(() => { fetch(url); }, [url]);
                +return <button disabled={loading} onClick={submit}>Run</button>;
                """),
            ("Dockerfile", """
                diff --git a/Dockerfile b/Dockerfile
                +++ b/Dockerfile
                @@ -1,1 +1,5 @@
                +FROM mcr.microsoft.com/dotnet/sdk:10.0
                +ARG NUGET_INTERNAL_FEED_URL
                +COPY . .
                +RUN dotnet publish src/App.csproj
                +HEALTHCHECK CMD curl -f http://localhost:8080/health || exit 1
                """),
            ("Directory.Packages.props", """
                diff --git a/Directory.Packages.props b/Directory.Packages.props
                +++ b/Directory.Packages.props
                @@ -1,1 +1,4 @@
                +<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
                """)
        };

        var domains = ReviewRiskDomainClassifier.Classify(files);
        var domainKinds = domains.Select(domain => domain.Domain).ToHashSet();

        Assert.Contains(ReviewRiskDomain.AuthTokenSecurity, domainKinds);
        Assert.Contains(ReviewRiskDomain.ConfigDiOptions, domainKinds);
        Assert.Contains(ReviewRiskDomain.HttpIntegration, domainKinds);
        Assert.Contains(ReviewRiskDomain.CacheStateTtl, domainKinds);
        Assert.Contains(ReviewRiskDomain.SqlEfDataIntegrity, domainKinds);
        Assert.Contains(ReviewRiskDomain.KafkaCdcEvents, domainKinds);
        Assert.Contains(ReviewRiskDomain.ApiContractValidation, domainKinds);
        Assert.Contains(ReviewRiskDomain.TestsFixtures, domainKinds);
        Assert.Contains(ReviewRiskDomain.BackgroundJobsConcurrency, domainKinds);
        Assert.Contains(ReviewRiskDomain.ObservabilityOperability, domainKinds);
        Assert.Contains(ReviewRiskDomain.SerializationMapping, domainKinds);
        Assert.Contains(ReviewRiskDomain.FrontendUiState, domainKinds);
        Assert.Contains(ReviewRiskDomain.BuildPackaging, domainKinds);
        Assert.All(domains, domain => Assert.NotEmpty(domain.Factors));

        Assert.Contains(domains.Single(domain => domain.Domain == ReviewRiskDomain.BackgroundJobsConcurrency).Factors, factor =>
            factor.Id.Contains("concurrency-primitives", StringComparison.Ordinal));
        Assert.Contains(domains.Single(domain => domain.Domain == ReviewRiskDomain.ObservabilityOperability).Factors, factor =>
            factor.Id.Contains("sensitive-log-fields", StringComparison.Ordinal));
        Assert.Contains(domains.Single(domain => domain.Domain == ReviewRiskDomain.SerializationMapping).Factors, factor =>
            factor.Id.Contains("json-serialization", StringComparison.Ordinal));
        Assert.Contains(domains.Single(domain => domain.Domain == ReviewRiskDomain.BuildPackaging).Factors, factor =>
            factor.Id.Contains("central-package-management", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_TreatsTaskFactoryStartNew_AsConcurrencySignal()
    {
        var files = new (string FilePath, string Content)[]
        {
            ("src/Auth/Helpers/AsyncLazy.cs", """
                diff --git a/src/Auth/Helpers/AsyncLazy.cs b/src/Auth/Helpers/AsyncLazy.cs
                +++ b/src/Auth/Helpers/AsyncLazy.cs
                @@ -1,1 +1,5 @@
                +public AsyncLazy(Func<Task<string>> taskFactory) :
                +    base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap())
                +{ }
                """)
        };

        var domains = ReviewRiskDomainClassifier.Classify(files);
        var concurrency = Assert.Single(domains, domain =>
            domain.Domain == ReviewRiskDomain.BackgroundJobsConcurrency);

        Assert.Contains(concurrency.Factors, factor =>
            factor.Id.Contains("concurrency-primitives", StringComparison.Ordinal));
        Assert.Contains(concurrency.Checklist, item =>
            item.Contains("CPU-bound", StringComparison.OrdinalIgnoreCase));
    }
}
