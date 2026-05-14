using System.Text.RegularExpressions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Services;

public static partial class ReviewRiskDomainClassifier
{
    private const int MaxFactorsPerDomain = 18;

    private static readonly IReadOnlyDictionary<ReviewRiskDomain, DomainMetadata> Metadata =
        new Dictionary<ReviewRiskDomain, DomainMetadata>
        {
            [ReviewRiskDomain.AuthTokenSecurity] = new(
                "Auth/token/security",
                "Сначала проверь субъект и границы доверия: кто действует, какой token используется, как он обновляется и где кэшируется.",
                [
                    "identity source: user token vs service token, fallback identity, impersonation",
                    "token acquisition/refresh: expiration threshold, UTC vs local time, cancellation, retry loop",
                    "token cache key: tenant/user/system/scope dimensions and invalidation",
                    "authorization: scopes, policies, claims, fail-closed behavior, sensitive secrets"
                ]),
            [ReviewRiskDomain.ConfigDiOptions] = new(
                "Config/DI/options",
                "Сначала проверь, что новая возможность реально подключена и не молча работает на дефолтах.",
                [
                    "DI registration: lifetime, duplicate registration, missing implementation",
                    "options binding: section name, env override shape, required keys",
                    "startup validation: Validate/ValidateOnStart or explicit safe defaults",
                    "runtime behavior: config value is actually used by control flow"
                ]),
            [ReviewRiskDomain.HttpIntegration] = new(
                "HTTP/integration",
                "Сначала проверь внешний контракт: как формируется запрос, что происходит при timeout/error и можно ли безопасно повторять вызов.",
                [
                    "client setup: base address, auth headers, named client/options, timeout",
                    "request flow: cancellation token, retries, idempotency, serialization",
                    "response flow: status mapping, error body handling, null/empty payloads",
                    "integration contract: DTO names, required fields, backward compatibility"
                ]),
            [ReviewRiskDomain.CacheStateTtl] = new(
                "Cache/state/TTL",
                "Сначала проверь ключи, срок жизни и инвалидацию: не смешиваются ли разные пользовательские или бизнес-контексты.",
                [
                    "cache key dimensions: user/tenant/system/filter/scope/page/locale",
                    "freshness: TTL, refresh interval, stale reads, invalidation after writes",
                    "concurrency: race on refresh, shared mutable state, partial update",
                    "failure mode: empty cache, warmup, fallback, serialization compatibility"
                ]),
            [ReviewRiskDomain.SqlEfDataIntegrity] = new(
                "SQL/EF/data integrity",
                "Сначала проверь сохранение семантики данных: фильтры, cardinality, ordering и атомарность.",
                [
                    "business filters: removed WHERE/JOIN predicates and fail-open branches",
                    "cardinality: row multiplication, duplicate keys, GroupBy/First without ordering",
                    "transactions: multi-step writes, bulk update/delete plus insert/save",
                    "EF shape: tracking, Include/N+1, migrations, nullability, indexes/constraints"
                ]),
            [ReviewRiskDomain.KafkaCdcEvents] = new(
                "Kafka/CDC/events",
                "Сначала проверь event-flow: порядок, идемпотентность, ownership источника и момент publish/commit.",
                [
                    "ordering: partition key, offset handling, child-before-parent CDC",
                    "idempotency: duplicate delivery, dedup key, replay behavior",
                    "side effects: publish/cache before commit/filter, DLQ/retry semantics",
                    "ownership: local writes vs CDC source of truth conflicts"
                ]),
            [ReviewRiskDomain.ApiContractValidation] = new(
                "API/contract/validation",
                "Сначала проверь публичный контракт: что клиент может отправить и что реально получит.",
                [
                    "request validation: required fields, ranges, PageSize limits, default values",
                    "response contract: nullable vs non-nullable, status codes, error shape",
                    "compatibility: route/query/body changes, enum/string naming, versioning",
                    "consumer path: controller/endpoint -> handler -> repository/service"
                ]),
            [ReviewRiskDomain.TestsFixtures] = new(
                "Tests/fixtures",
                "Сначала проверь, что тест действительно доказывает новый behavior, а fixture может создать заявленное состояние.",
                [
                    "coverage: critical path, negative path, config/DI startup, integration errors",
                    "fixtures: seed values match assertions and production query filters",
                    "stability: Task.Delay, timing assumptions, shared state, order dependence",
                    "assertions: test fails for the old bug and cannot pass accidentally"
                ]),
            [ReviewRiskDomain.BackgroundJobsConcurrency] = new(
                "Background jobs/concurrency",
                "Сначала проверь lifecycle и многопоточность: остановка, cancel, parallel execution и shared state.",
                [
                    "lifecycle: ExecuteAsync/StopAsync, cancellation propagation, graceful shutdown",
                    "scheduling: intervals, cron/timezone, overlap prevention, scoped service creation, Task.Run/StartNew only for CPU-bound work",
                    "concurrency: locks, SemaphoreSlim, ConcurrentDictionary, Channel, Task.WhenAll errors",
                    "failure mode: swallowed exceptions, retry loop, checkpoint/cursor partial progress"
                ]),
            [ReviewRiskDomain.ObservabilityOperability] = new(
                "Observability/operability",
                "Сначала проверь, можно ли понять и безопасно эксплуатировать новый behavior в production.",
                [
                    "logs: useful context without secrets/PII, correlation id, error level and exception object",
                    "metrics/tracing: health checks, counters, histograms, spans for background/integration flows",
                    "timeouts/retries: visible settings, operational defaults, startup diagnostics",
                    "runbook signals: failure status, degraded mode, actionable messages"
                ]),
            [ReviewRiskDomain.SerializationMapping] = new(
                "Serialization/mapping",
                "Сначала проверь преобразование данных между слоями: имена полей, nullability, enum/date/decimal semantics.",
                [
                    "mapping: AutoMapper/Profile/MapFrom coverage, dropped fields, reverse map",
                    "serialization: JsonPropertyName/JsonIgnore, JsonConverter, enum names, date/time zones",
                    "contract drift: DTO vs domain/read model, required fields, defaults",
                    "precision: decimal/money, DateOnly/TimeOnly, nullable collections and collection defaults"
                ]),
            [ReviewRiskDomain.FrontendUiState] = new(
                "Frontend/UI/state",
                "Сначала проверь пользовательский flow: состояние, loading/error, stale request и доступность управления.",
                [
                    "state: stale closures, useEffect dependencies, race between requests",
                    "API flow: abort/cancel, error handling, optimistic update rollback",
                    "UX states: loading, empty, disabled, validation, responsive text",
                    "accessibility: labels, keyboard path, focus, aria for controls"
                ]),
            [ReviewRiskDomain.BuildPackaging] = new(
                "Build/packaging/deploy",
                "Сначала проверь, что изменение воспроизводимо собирается и корректно доезжает до runtime.",
                [
                    "restore/build: package versions, central package management, target frameworks, internal feeds",
                    "container/runtime: Dockerfile copy order, build args, env names, ports, volumes",
                    "pipeline/deploy: compose/k8s/helm/env compatibility and secrets handling",
                    "frontend packages: lockfile, script names, build output and Node version"
                ])
        };

    private static readonly IReadOnlyList<PathFactorRule> PathRules =
    [
        new(ReviewRiskDomain.AuthTokenSecurity, "path-auth", "path указывает на auth/security/identity область", 4, @"(^|/)(auth|authentication|authorization|identity|security|permissions?|policies|claims?)(/|\.|$)|token|jwt|oauth|openid|certificate"),
        new(ReviewRiskDomain.ConfigDiOptions, "path-config-di", "path указывает на DI/config/options", 4, @"appsettings|docker-compose|\.env|dependencyinjection|servicecollection|options|settings|configuration|extensions/.*dependencies|adddependencies|startup|program\.cs$"),
        new(ReviewRiskDomain.HttpIntegration, "path-http-integration", "path указывает на HTTP/integration/client", 4, @"(^|/)(clients?|integrations?|http|rest|refit|grpc|soap|connected services)(/|\.|$)|client\.cs$"),
        new(ReviewRiskDomain.CacheStateTtl, "path-cache", "path указывает на cache/state/redis", 4, @"(^|/)(cache|caching|redis|state|stores?)(/|\.|$)|cache"),
        new(ReviewRiskDomain.SqlEfDataIntegrity, "path-sql-ef", "path указывает на SQL/EF/persistence", 4, @"\.sql$|(^|/)(migrations?|repositories?|persistence|entities|db|database|infrastructure)(/|\.|$)|dbcontext"),
        new(ReviewRiskDomain.KafkaCdcEvents, "path-events", "path указывает на Kafka/CDC/events/consumers", 4, @"(^|/)(kafka|cdc|events?|consumers?|producers?|outbox|inbox|messaging|queues?)(/|\.|$)"),
        new(ReviewRiskDomain.ApiContractValidation, "path-api-contract", "path указывает на API contract/controller/validator", 4, @"(^|/)(controllers?|endpoints?|contracts?|dtos?|models?|requests?|responses?|validators?|swagger|openapi)(/|\.|$)"),
        new(ReviewRiskDomain.TestsFixtures, "path-tests", "path указывает на tests/fixtures/seed", 5, @"(^|/)(tests?|test|fixtures?|seed|seeds|init)(/|\.|$)|tests?\.cs$|spec\.ts$|test\.ts$"),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "path-background", "path указывает на background jobs/scheduler", 4, @"(^|/)(background|jobs?|workers?|hostedservices?|quartz|scheduler|scheduling|cron|processors?|pollers?|dispatchers?)(/|\.|$)|worker\.cs$|job\.cs$|processor\.cs$"),
        new(ReviewRiskDomain.ObservabilityOperability, "path-observability", "path указывает на observability/health/logging", 3, @"(^|/)(logging|observability|telemetry|metrics|health|monitoring|tracing|diagnostics|serilog|prometheus|grafana|applicationinsights)(/|\.|$)"),
        new(ReviewRiskDomain.SerializationMapping, "path-mapping", "path указывает на mapping/serialization/profile", 3, @"(^|/)(mapping|mappers?|profiles?|serialization|serializers?|converters?|formatters?|transformers?)(/|\.|$)|profile\.cs$|converter\.cs$"),
        new(ReviewRiskDomain.FrontendUiState, "path-frontend", "path указывает на frontend/UI", 4, @"(^|/)(frontend|src/components|src/pages|ui|views?)(/|\.|$)|\.(tsx|jsx|vue|svelte|css|scss)$"),
        new(ReviewRiskDomain.BuildPackaging, "path-build", "path указывает на build/package/deploy артефакт", 4, @"\.csproj$|\.sln$|\.props$|\.targets$|directory\.packages\.props|dockerfile$|docker-compose|compose\.ya?ml|nuget\.config|package\.json|package-lock\.json|pnpm-lock|yarn.lock|vite\.config|\.ya?ml$|pipeline|workflow|deploy|helm|chart\.yaml|values\.yaml|kustomization|terraform|\.tf$")
    ];

    private static readonly IReadOnlyList<TextFactorRule> TextRules =
    [
        new(ReviewRiskDomain.AuthTokenSecurity, "jwt-token-symbols", "JWT/token API рядом с изменением", 5, @"JwtSecurityToken|TokenValidationParameters|SecurityToken|ValidTo|ValidFrom|\bexp\b|\bnbf\b|access_token|refresh_token|Bearer|OAuth|OpenId"),
        new(ReviewRiskDomain.AuthTokenSecurity, "identity-claims", "изменение работает с identity/claims/principal", 4, @"ClaimsPrincipal|ClaimTypes|IHttpContextAccessor|HttpContext\.User|CurrentUser|UserId|principal|identity|impersonat"),
        new(ReviewRiskDomain.AuthTokenSecurity, "auth-registration", "изменение включает auth registration/policy/handler", 4, @"AddAuthentication|AddAuthorization|AuthorizationHandler|AuthenticationScheme|AuthorizeAttribute|\[Authorize\]|policy|scope"),
        new(ReviewRiskDomain.AuthTokenSecurity, "secret-auth-material", "изменение рядом с auth secret/key/certificate", 4, @"client_secret|signing.?key|certificate|private.?key|ApiKey|X-Api-Key|secret"),

        new(ReviewRiskDomain.ConfigDiOptions, "di-registration", "DI registration/lifetime изменён", 5, @"IServiceCollection|AddSingleton|AddScoped|AddTransient|TryAdd|Replace\(|Decorate\(|Register"),
        new(ReviewRiskDomain.ConfigDiOptions, "options-binding", "options/config binding изменён", 5, @"Configure<|BindConfiguration|GetSection|GetRequiredSection|IOptions|IOptionsMonitor|IOptionsSnapshot|OptionsBuilder"),
        new(ReviewRiskDomain.ConfigDiOptions, "options-validation", "options validation/startup validation упомянуты", 4, @"ValidateOnStart|ValidateDataAnnotations|OptionsValidation|IValidateOptions|Validate\("),
        new(ReviewRiskDomain.ConfigDiOptions, "env-config-key", "env/config key изменён", 3, @"configuration\[|Environment\.GetEnvironmentVariable|__|ConnectionStrings|appsettings|ASPNETCORE_|DOTNET_"),

        new(ReviewRiskDomain.HttpIntegration, "http-client", "HTTP client/request flow изменён", 5, @"HttpClient|IHttpClientFactory|AddHttpClient|SendAsync|GetAsync|PostAsync|PutAsync|PatchAsync|DeleteAsync|HttpRequestMessage"),
        new(ReviewRiskDomain.HttpIntegration, "http-resilience", "timeout/retry/resilience signal рядом с HTTP", 4, @"Timeout|Polly|Retry|CircuitBreaker|EnsureSuccessStatusCode|HttpStatusCode|BaseAddress|Headers\.Authorization"),
        new(ReviewRiskDomain.HttpIntegration, "integration-serialization", "HTTP serialization/deserialization изменены", 3, @"ReadFromJsonAsync|PostAsJsonAsync|JsonContent|JsonSerializer\.Deserialize|StringContent|MediaTypeHeaderValue"),
        new(ReviewRiskDomain.HttpIntegration, "external-contract", "external endpoint/URL/route используется", 3, @"BaseUrl|Endpoint|Url|Uri|RequestUri|SOAP|gRPC|Refit|RestClient"),

        new(ReviewRiskDomain.CacheStateTtl, "cache-api", "cache/redis API изменён", 5, @"IMemoryCache|IDistributedCache|Redis|StackExchange|MemoryCache|HybridCache|IDatabase"),
        new(ReviewRiskDomain.CacheStateTtl, "cache-key", "cache key или dimension изменены", 5, @"cache.?key|CacheKey|key\s*=|GetKey|BuildKey|sessionId|tenantId|userId|systemId|scope"),
        new(ReviewRiskDomain.CacheStateTtl, "ttl-expiration", "TTL/expiration/refresh semantics изменены", 4, @"TTL|TimeToLive|AbsoluteExpiration|SlidingExpiration|Expire|Expiration|Refresh|TimeSpan|DateTimeOffset"),
        new(ReviewRiskDomain.CacheStateTtl, "shared-state", "shared mutable/in-memory state изменён", 3, @"ConcurrentDictionary|FrozenDictionary|Dictionary<|volatile|lock\s*\(|SemaphoreSlim|Lazy<"),

        new(ReviewRiskDomain.SqlEfDataIntegrity, "sql-query", "SQL query/filter/join изменён", 5, @"\bselect\b|\bwhere\b|\bjoin\b|left join|inner join|order by|group by|having|limit|offset|IsBasic|IsBcAllowed"),
        new(ReviewRiskDomain.SqlEfDataIntegrity, "ef-dbcontext", "EF DbContext/DbSet/query изменён", 5, @"DbContext|DbSet|IQueryable|AsNoTracking|Include\(|ThenInclude|FromSql|ExecuteUpdate|ExecuteDelete|SaveChanges"),
        new(ReviewRiskDomain.SqlEfDataIntegrity, "transaction-signal", "transaction/atomicity signal изменён", 4, @"BeginTransaction|TransactionScope|CommitAsync|RollbackAsync|ExecuteUpdate|ExecuteDelete|SaveChangesAsync"),
        new(ReviewRiskDomain.SqlEfDataIntegrity, "data-shape", "data shape/order/grouping signal изменён", 4, @"GroupBy|ToDictionary|DistinctBy|First\(|FirstOrDefault|Single\(|OrderBy|HasIndex|HasForeignKey|migrationBuilder"),

        new(ReviewRiskDomain.KafkaCdcEvents, "kafka-symbols", "Kafka/topic/consumer/producer изменены", 5, @"Kafka|Topic|Consumer|Producer|Consume|Produce|Confluent|IKafka|MessageHandler"),
        new(ReviewRiskDomain.KafkaCdcEvents, "cdc-outbox", "CDC/outbox/inbox/event sourcing signal изменён", 5, @"CDC|Debezium|Outbox|Inbox|EventStore|DomainEvent|IntegrationEvent|ChangeDataCapture"),
        new(ReviewRiskDomain.KafkaCdcEvents, "event-delivery", "event delivery/order/idempotency signal изменён", 4, @"Offset|Partition|Commit|Retry|DLQ|DeadLetter|Idempot|Dedup|ExactlyOnce|AtLeastOnce"),
        new(ReviewRiskDomain.KafkaCdcEvents, "publish-subscribe", "publish/subscribe side effect изменён", 4, @"Publish|Subscribe|SendMessage|Enqueue|Dequeue|Queue|ServiceBus|RabbitMQ|MassTransit"),

        new(ReviewRiskDomain.ApiContractValidation, "api-endpoint", "controller/endpoint/route изменён", 5, @"Controller|ApiController|MapGet|MapPost|MapPut|MapDelete|HttpGet|HttpPost|Route\(|ProducesResponseType|StatusCodes"),
        new(ReviewRiskDomain.ApiContractValidation, "request-binding", "request binding/query/body/header изменены", 4, @"FromQuery|FromBody|FromRoute|FromHeader|BindRequired|Required|JsonPropertyName|JsonIgnore"),
        new(ReviewRiskDomain.ApiContractValidation, "validation-rules", "validation rules изменены", 5, @"AbstractValidator|RuleFor|NotNull|NotEmpty|GreaterThan|LessThan|MaximumLength|MinimumLength|PageSize|Validate"),
        new(ReviewRiskDomain.ApiContractValidation, "public-contract", "public DTO/interface/record contract изменён", 3, @"public\s+(record|class|interface|enum)|required\s+|init;|IResult|ActionResult|ProblemDetails"),

        new(ReviewRiskDomain.TestsFixtures, "test-framework", "test/assertion code изменён", 5, @"\[Fact\]|\[Theory\]|\[Test\]|Assert\.|Should\(\)|FluentAssertions|NSubstitute|Moq|Verify\(|Setup\("),
        new(ReviewRiskDomain.TestsFixtures, "test-fixture", "fixture/seed/test data изменены", 5, @"Seed|Fixture|TestData|Bogus|AutoFixture|WebApplicationFactory|TestServer|Testcontainers|Respawn"),
        new(ReviewRiskDomain.TestsFixtures, "test-timing", "timing/concurrency в тесте изменены", 4, @"Task\.Delay|Thread\.Sleep|Eventually|WaitAsync|CancellationTokenSource|StopAsync|StartAsync"),
        new(ReviewRiskDomain.TestsFixtures, "test-http-db", "integration test touches HTTP/DB/container", 3, @"HttpClient|PostAsJsonAsync|GetAsync|DbContext|Database|Container|Docker|WireMock"),

        new(ReviewRiskDomain.BackgroundJobsConcurrency, "hosted-service", "hosted/background service lifecycle изменён", 5, @"BackgroundService|IHostedService|ExecuteAsync|StartAsync|StopAsync|PeriodicTimer|Timer\("),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "scheduler-job", "scheduler/job/cron изменён", 5, @"Quartz|IJob|Cron|Schedule|Scheduler|RecurringJob|Hangfire"),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "concurrency-primitives", "concurrency primitive/shared execution изменены", 4, @"Task\.Run|Task\.Factory\.StartNew|Task\.WhenAll|Task\.WhenAny|Parallel\.|Parallel.ForEachAsync|SemaphoreSlim|Channel<|lock\s*\(|Interlocked|Concurrent|ReaderWriterLockSlim"),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "cancellation-flow", "cancellation/timeout flow изменён", 3, @"CancellationToken|CancellationTokenSource|CancelAfter|CancelAsync|OperationCanceledException|TimeoutException|WithCancellation"),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "scoped-background-dependencies", "background flow создаёт scope или берёт scoped dependency", 3, @"IServiceScopeFactory|CreateScope|CreateAsyncScope|IServiceProvider|GetRequiredService"),
        new(ReviewRiskDomain.BackgroundJobsConcurrency, "progress-checkpoint", "background flow хранит progress/checkpoint/cursor", 3, @"Checkpoint|Cursor|LastProcessed|Watermark|BatchSize|PageSize|Offset"),

        new(ReviewRiskDomain.ObservabilityOperability, "logging", "logging/correlation изменены", 4, @"ILogger|LogInformation|LogWarning|LogError|LogDebug|LogTrace|BeginScope|CorrelationId|TraceId|RequestId"),
        new(ReviewRiskDomain.ObservabilityOperability, "metrics-tracing", "metrics/tracing/health изменены", 4, @"Meter|Counter<|Histogram<|ObservableGauge|ActivitySource|Activity\.Current|OpenTelemetry|AddHealthChecks|IHealthCheck|Prometheus"),
        new(ReviewRiskDomain.ObservabilityOperability, "operational-timeouts", "operational timeout/retry setting изменён", 3, @"Timeout|Retry|Backoff|CircuitBreaker|HealthCheck|Readiness|Liveness"),
        new(ReviewRiskDomain.ObservabilityOperability, "sensitive-log-fields", "logging рядом с secret/PII/token полями", 5, @"Log(?:Trace|Debug|Information|Warning|Error|Critical)\(.*(password|token|secret|authorization|phone|email|msisdn|passport)"),
        new(ReviewRiskDomain.ObservabilityOperability, "diagnostic-status", "изменяется diagnostic/status/health response", 3, @"HealthStatus|Degraded|Unhealthy|Diagnostic|StatusCode|ProblemDetails|ErrorCode"),

        new(ReviewRiskDomain.SerializationMapping, "mapper", "mapping profile/conversion изменён", 5, @"AutoMapper|Profile|CreateMap|ForMember|MapFrom|IMapper|Adapt<|Mapster"),
        new(ReviewRiskDomain.SerializationMapping, "json-serialization", "JSON serialization attributes/API изменены", 4, @"JsonSerializer|JsonPropertyName|JsonIgnore|JsonConverter|JsonExtensionData|JsonConstructor|JsonStringEnumConverter|Newtonsoft|System.Text.Json"),
        new(ReviewRiskDomain.SerializationMapping, "data-type-conversion", "date/enum/decimal conversion изменён", 4, @"DateOnly|TimeOnly|DateTimeOffset|DateTime|EnumMember|Enum\.Parse|decimal|Money|Parse\(|TryParse"),
        new(ReviewRiskDomain.SerializationMapping, "nullable-contract-shape", "mapping/DTO shape меняет nullable/default collection semantics", 3, @"required\s+|init;|\?\s*\{|IReadOnlyList|IEnumerable|Array\.Empty|new\(\)|default!"),
        new(ReviewRiskDomain.SerializationMapping, "manual-projection", "manual projection between domain/read model/DTO изменён", 3, @"new\s+[A-Za-z0-9_]*(Dto|Response|Request|Model|View)\s*\{|Select\([^=]*=>\s*new|ToDto|FromDto"),

        new(ReviewRiskDomain.FrontendUiState, "react-state", "React state/effect изменён", 5, @"useState|useEffect|useMemo|useCallback|useReducer|set[A-Z]\w+|props\.|state"),
        new(ReviewRiskDomain.FrontendUiState, "frontend-api", "frontend API async flow изменён", 4, @"fetch\(|axios|useQuery|useMutation|AbortController|Promise|async\s+function|await\s+"),
        new(ReviewRiskDomain.FrontendUiState, "ui-form-control", "UI controls/form/validation изменены", 4, @"onClick|onChange|disabled|aria-|role=|input|select|button|form|label|validation"),
        new(ReviewRiskDomain.FrontendUiState, "frontend-storage", "frontend persisted/local state изменён", 3, @"localStorage|sessionStorage|URLSearchParams|history\.push|navigate\("),

        new(ReviewRiskDomain.BuildPackaging, "dotnet-build", ".NET build/package metadata изменены", 5, @"PackageReference|PackageVersion|ProjectReference|TargetFramework|RuntimeIdentifier|LangVersion|Nullable|ImplicitUsings"),
        new(ReviewRiskDomain.BuildPackaging, "central-package-management", "central package/build props изменены", 4, @"Directory\.Packages\.props|Directory\.Build\.props|ManagePackageVersionsCentrally|PackageVersion|VersionOverride|PrivateAssets|IncludeAssets"),
        new(ReviewRiskDomain.BuildPackaging, "container-build", "container/compose runtime metadata изменены", 5, @"^\s*(FROM|COPY|RUN|ENTRYPOINT|ENV|ARG|EXPOSE|HEALTHCHECK)\b|^\s*(depends_on|ports|volumes):"),
        new(ReviewRiskDomain.BuildPackaging, "frontend-package", "frontend package/build metadata изменены", 4, @"""scripts""|""dependencies""|""devDependencies""|""engines""|npm|pnpm|yarn|vite|webpack|node-version"),
        new(ReviewRiskDomain.BuildPackaging, "pipeline-deploy", "pipeline/deploy metadata изменены", 4, @"stages:|jobs:|steps:|workflow|pipeline|helm|kustomize|kubectl|image:|resources:|variables:|secrets:")
    ];

    public static IReadOnlyList<ReviewRiskDomainInsight> Classify(
        IReadOnlyList<(string FilePath, string Content)> files)
    {
        var factors = new List<ReviewRiskFactor>();
        foreach (var file in files)
        {
            AddPathFactors(file.FilePath, factors);

            var scanLines = EnumerateScanLines(file.Content)
                .Where(line => line.Kind is '+' or '-' or ' ')
                .ToArray();
            AddTextFactors(file.FilePath, scanLines, factors);
        }

        return factors
            .GroupBy(factor => GetDomainByFactorId(factor.Id))
            .Select(group =>
            {
                var domain = group.Key;
                var metadata = Metadata[domain];
                var domainFactors = group
                    .OrderByDescending(factor => factor.Weight)
                    .ThenBy(factor => factor.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(factor => factor.StartLine)
                    .Take(MaxFactorsPerDomain)
                    .ToArray();

                return new ReviewRiskDomainInsight
                {
                    Domain = domain,
                    Title = metadata.Title,
                    ReviewMode = metadata.ReviewMode,
                    Checklist = metadata.Checklist,
                    Score = group.Sum(factor => factor.Weight),
                    Factors = domainFactors
                };
            })
            .Where(domain => domain.Score >= 4 || domain.Factors.Any(factor => factor.Weight >= 5))
            .OrderByDescending(domain => domain.Score)
            .ThenBy(domain => domain.Title, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddPathFactors(string filePath, List<ReviewRiskFactor> factors)
    {
        var normalizedPath = Normalize(filePath);
        foreach (var rule in PathRules)
        {
            if (!Regex.IsMatch(normalizedPath, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            AddFactor(
                factors,
                rule.Domain,
                rule.Id,
                rule.Description,
                filePath,
                1,
                filePath,
                rule.Weight);
        }
    }

    private static void AddTextFactors(
        string filePath,
        IReadOnlyList<ScanLine> lines,
        List<ReviewRiskFactor> factors)
    {
        foreach (var rule in TextRules)
        {
            var line = lines.FirstOrDefault(line =>
                Regex.IsMatch(line.Text, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (line is null)
            {
                continue;
            }

            AddFactor(
                factors,
                rule.Domain,
                rule.Id,
                rule.Description,
                filePath,
                line.NewLine,
                line.Text.Trim(),
                rule.Weight);
        }
    }

    private static void AddFactor(
        List<ReviewRiskFactor> factors,
        ReviewRiskDomain domain,
        string id,
        string description,
        string filePath,
        int startLine,
        string evidence,
        int weight)
    {
        var factorId = $"{domain}:{id}";
        if (factors.Any(existing =>
                existing.Id == factorId &&
                PathsMatch(existing.FilePath, filePath)))
        {
            return;
        }

        factors.Add(new ReviewRiskFactor
        {
            Id = factorId,
            Description = description,
            FilePath = filePath,
            StartLine = Math.Max(1, startLine),
            Evidence = TrimEvidence(evidence),
            Weight = weight
        });
    }

    private static ReviewRiskDomain GetDomainByFactorId(string factorId)
    {
        var separator = factorId.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 &&
               Enum.TryParse<ReviewRiskDomain>(factorId[..separator], out var domain)
            ? domain
            : ReviewRiskDomain.ApiContractValidation;
    }

    private static IReadOnlyList<ScanLine> EnumerateScanLines(string content)
    {
        var result = new List<ScanLine>();
        var newLine = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (match.Success && int.TryParse(match.Groups["new"].Value, out var parsedNew))
                {
                    newLine = parsedNew - 1;
                }

                continue;
            }

            if (line.Length == 0 || line[0] is not ('+' or '-' or ' '))
            {
                continue;
            }

            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var kind = line[0];
            if (kind is '+' or ' ')
            {
                newLine++;
            }

            var effectiveLine = newLine > 0 ? newLine : 1;
            result.Add(new ScanLine(kind, line.Length > 1 ? line[1..] : string.Empty, effectiveLine));
        }

        return result;
    }

    private static bool PathsMatch(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
        => value.Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();

    private static string TrimEvidence(string value)
    {
        var trimmed = Regex.Replace(value.Trim(), @"\s+", " ");
        return trimmed.Length <= 180
            ? trimmed
            : trimmed[..180] + "…";
    }

    [GeneratedRegex(@"@@\s+-\d+(?:,\d+)?\s+\+(?<new>\d+)(?:,\d+)?\s+@@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeaderRegex();

    private sealed record DomainMetadata(
        string Title,
        string ReviewMode,
        IReadOnlyList<string> Checklist);

    private sealed record PathFactorRule(
        ReviewRiskDomain Domain,
        string Id,
        string Description,
        int Weight,
        string Pattern);

    private sealed record TextFactorRule(
        ReviewRiskDomain Domain,
        string Id,
        string Description,
        int Weight,
        string Pattern);

    private sealed record ScanLine(char Kind, string Text, int NewLine);
}
