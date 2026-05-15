using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class FindingNormalizationDeduplicationTests
{
    [Fact]
    public void SuppressLowPrecisionFindings_RemovesKnownSpeculativeNoise()
    {
        var findings = new[]
        {
            CreateFinding(
                "Auth/ServiceToken/EsbTokenService.cs",
                19,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Утечка ресурса: SemaphoreSlim не освобождается",
                "Класс хранит SemaphoreSlim, но не реализует IDisposable.",
                "private readonly SemaphoreSlim _semaphore = new(1, 1);"),
            CreateFinding(
                "Auth/Helpers/AsyncLazy.cs",
                9,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Выполнение фабрики может захватить UI-поток",
                "Task.Factory.StartNew без TaskScheduler.Default может захватить UI-поток.",
                "base(() => Task.Factory.StartNew(valueFactory))"),
            CreateFinding(
                "Auth/ServiceToken/EsbTokenService.cs",
                33,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Возврат устаревшего токена при сбое обновления",
                "Если GetTokenAsync выбрасывает исключение, метод вернёт кэшированный токен.",
                "try\n{\n    var token = await _esbAuthClient.GetTokenAsync(cancellationToken);\n    return _accessToken;\n}\nfinally\n{\n    _semaphore.Release();\n}"),
            CreateFinding(
                "Auth/Helpers/AsyncLazy.cs",
                11,
                FindingCategory.CodeStyle,
                FindingSeverity.Low,
                "Упростить Task.Factory.StartNew через Task.Run",
                "Task.Run выглядит современнее и читабельнее.",
                "base(() => Task.Factory.StartNew(valueFactory))"),
            CreateFinding(
                "Auth/BaseServiceTokenService.cs",
                54,
                FindingCategory.Reliability,
                FindingSeverity.High,
                "Сравнение времени жизни JWT-токена с локальным временем вместо UTC",
                "JWT ValidTo всегда в UTC, но код сравнивает его с DateTime.Now.",
                "if (_tokenObject.ValidTo < DateTime.Now.AddMinutes(1))")
        };

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings(findings);

        var finding = Assert.Single(result);
        Assert.Contains("JWT", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_RemovesStyleAndDocumentationNits()
    {
        var opportunities = new[]
        {
            new ReviewOpportunityItem(
                "Auth/Abstractions/IEsbTokenService.cs",
                "GetAccessTokenAsync",
                "Добавить описание возвращаемого значения",
                "Отсутствует xml-тег <returns>.",
                "Добавить <returns>Access token</returns>.",
                9),
            new ReviewOpportunityItem(
                "Auth/Models/EsbAuthTokenModel.cs",
                "EsbAuthTokenModel",
                "Неиспользуемый импорт System.Text.Json.Serialization",
                "Директива using не используется.",
                "Удалить using.",
                1),
            new ReviewOpportunityItem(
                "Auth/Helpers/AsyncLazy.cs",
                "AsyncLazy",
                "Упрощение фабрики с помощью Task.Run",
                "Конструктор использует Task.Factory.StartNew(valueFactory), можно заменить на Task.Run(valueFactory).",
                "Заменить Task.Factory.StartNew(valueFactory) на Task.Run(valueFactory).",
                9),
            new ReviewOpportunityItem(
                "Auth/ServiceToken/BaseServiceTokenService.cs",
                "GetToken",
                "Удержание семафора на время запроса токена",
                "SemaphoreSlim захвачен на время потенциально длительного RequestToken.",
                "Защищать семафором только замену lazy-значения.",
                29)
        };

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities(opportunities);

        var opportunity = Assert.Single(result);
        Assert.Contains("семафора", opportunity.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_KeepsTaskFactoryStartNewAsyncIoConcern()
    {
        var findings = new[]
        {
            CreateFinding(
                "Auth/Helpers/AsyncLazy.cs",
                11,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Task.Factory.StartNew используется вокруг async I/O обновления токена",
                "Фабрика запускает асинхронный token refresh через Task.Factory.StartNew, хотя это не CPU-bound работа; при fire-and-forget обновлении исключение может остаться необработанным.",
                "base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap())")
        };

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings(findings);

        Assert.Single(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_RemovesHttpClientFactorySharedInstanceRaceMyth()
    {
        var findings = new[]
        {
            CreateFinding(
                "Auth/Services/IdentityServiceTokenService.cs",
                49,
                FindingCategory.Bug,
                FindingSeverity.Medium,
                "Гонка данных при установке BaseAddress на общем HttpClient",
                "IHttpClientFactory.CreateClient() возвращает общий default-экземпляр; параллельные вызовы могут перезаписать BaseAddress.",
                "var httpClient = _httpClientFactory.CreateClient();\nhttpClient.BaseAddress = new Uri(_identityOptions.Url);")
        };

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings(findings);

        Assert.Empty(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_RemovesDefaultHttpClientFactoryPolicyFinding()
    {
        var findings = new[]
        {
            CreateFinding(
                "Auth/Services/IdentityServiceTokenService.cs",
                49,
                FindingCategory.Performance,
                FindingSeverity.Medium,
                "Создание HttpClient без именованного клиента — обход пула соединений и политик",
                "CreateClient() без имени обходит пул соединений и политики Polly.",
                "var httpClient = _httpClientFactory.CreateClient();")
        };

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings(findings);

        Assert.Empty(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_RemovesLowCodeStyleFindings()
    {
        var findings = new[]
        {
            CreateFinding(
                "Auth/Auth.csproj",
                1,
                FindingCategory.CodeStyle,
                FindingSeverity.Low,
                "Несогласованность версий между ReleaseNotes и csproj",
                "ReleaseNotes и csproj содержат разные версии.",
                "<Version>1.2.3</Version>")
        };

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings(findings);

        Assert.Empty(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_KeepsSqlBusinessFilterEvenWhenSuggestionMentionsComment()
    {
        var finding = CreateFinding(
            "Infrastructure/Db/GetOrderList.sql",
            64,
            FindingCategory.Logic,
            FindingSeverity.Medium,
            "Удаление бизнес-фильтров IsBasic/IsBcAllowed из SQL-запросов меняет состав результатов",
            "В SQL удалены WHERE-условия rb.IsBasic и rb.IsBcAllowed. Если это намеренное изменение, нужен комментарий с бизнес-обоснованием.",
            "where rb.\"IsBasic\" is true and rb.\"IsBcAllowed\" is true");

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings([finding]);

        Assert.Single(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_RemovesDefensiveNullGuardOnlyFinding()
    {
        var finding = CreateFinding(
            "Domain/Extensions/RegionCacheExtensions.cs",
            132,
            FindingCategory.Reliability,
            FindingSeverity.Low,
            "Методы EnrichWithRegionData не проверяют regionCacheRepository на null",
            "Параметр regionCacheRepository не проверяется на null. Хотя в штатном режиме DI гарантирует передачу экземпляра, защитная проверка улучшит диагностику.",
            "order.OrderRegionName ??= regionCacheRepository.GetRegionName(order.OrderRegionCode);");

        var result = ReviewRunExecutor.SuppressLowPrecisionFindings([finding]);

        Assert.Empty(result);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_DemotesTestMaintainabilityOnlyFinding()
    {
        var finding = CreateFinding(
            "Tele2.Crm.CustomerRepresentService.Tests/CustomerMarkersServiceTests.cs",
            423,
            FindingCategory.Architecture,
            FindingSeverity.Medium,
            "Дублирование ручных реализаций async EF helpers при наличии MockQueryable.Moq",
            "CustomerMarkersServiceTests.cs содержит собственные реализации TestAsyncQueryProvider, TestAsyncEnumerable и TestAsyncEnumerator. В том же проекте CustomerServiceTests.cs уже используется MockQueryable.Moq, поэтому два подхода увеличивают поддержку.",
            "internal class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider");

        var findingsResult = ReviewRunExecutor.SuppressLowPrecisionFindings([finding]);
        var opportunities = ReviewRunExecutor.BuildOpportunitiesFromDemotableFindings([finding]);

        Assert.Empty(findingsResult);
        var opportunity = Assert.Single(opportunities);
        Assert.Equal("Tele2.Crm.CustomerRepresentService.Tests/CustomerMarkersServiceTests.cs", opportunity.File);
        Assert.Equal(finding.Title, opportunity.Title);
        Assert.Equal(423, opportunity.StartLine);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_KeepsProductionPackageFinding()
    {
        var finding = CreateFinding(
            "Tele2.Crm.CustomerRepresentService/Tele2.Crm.CustomerRepresentService.Domain/Tele2.Crm.CustomerRepresentService.Domain.csproj",
            16,
            FindingCategory.Bug,
            FindingSeverity.High,
            "Moq добавлен в production-зависимости Domain проекта",
            "Пакет Moq добавлен в production-проект, хотя тестовый проект уже содержит отдельный PackageReference.",
            "<PackageReference Include=\"Moq\" Version=\"4.20.72\" />");

        var findingsResult = ReviewRunExecutor.SuppressLowPrecisionFindings([finding]);
        var opportunities = ReviewRunExecutor.BuildOpportunitiesFromDemotableFindings([finding]);

        Assert.Single(findingsResult);
        Assert.Empty(opportunities);
    }

    [Fact]
    public void NormalizeFindingSeverities_DowngradesCriticalTestPackageInProductionProject()
    {
        var finding = CreateFinding(
            "Tele2.Crm.CustomerRepresentService/Tele2.Crm.CustomerRepresentService.Domain/Tele2.Crm.CustomerRepresentService.Domain.csproj",
            16,
            FindingCategory.Architecture,
            FindingSeverity.Critical,
            "Moq в production-проекте Domain.csproj",
            "Пакет Moq добавлен в production-проект и попадёт в production-сборку вместе с Castle.Core.",
            "<PackageReference Include=\"Moq\" Version=\"4.20.72\" />");

        var result = ReviewRunExecutor.NormalizeFindingSeverities([finding]);

        var normalized = Assert.Single(result);
        Assert.Equal(FindingSeverity.High, normalized.Severity);
    }

    [Fact]
    public void NormalizeFindingSeverities_KeepsCriticalSecurityFinding()
    {
        var finding = CreateFinding(
            "Tele2.Crm.Service/Auth/TokenService.cs",
            42,
            FindingCategory.Security,
            FindingSeverity.Critical,
            "Refresh token is logged",
            "Refresh token value is written to application logs.",
            "logger.LogInformation(\"refresh {Token}\", token);");

        var result = ReviewRunExecutor.NormalizeFindingSeverities([finding]);

        var normalized = Assert.Single(result);
        Assert.Equal(FindingSeverity.Critical, normalized.Severity);
    }

    [Fact]
    public void SuppressLowPrecisionFindings_KeepsConcreteFlakyTestFinding()
    {
        var finding = CreateFinding(
            "Tele2.Crm.Service.Tests/RegionCacheBackgroundServiceTests.cs",
            27,
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            "Нестабильный тест: фиксированная задержка вместо опроса флага загрузки кэша",
            "Тест использует Task.Delay(1000). На перегруженном CI-агенте тест может падать flaky, когда фоновой загрузке нужно больше секунды.",
            "await Task.Delay(1000, CancellationToken.None);");

        var findingsResult = ReviewRunExecutor.SuppressLowPrecisionFindings([finding]);
        var opportunities = ReviewRunExecutor.BuildOpportunitiesFromDemotableFindings([finding]);

        Assert.Single(findingsResult);
        Assert.Empty(opportunities);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_KeepsTaskFactoryStartNewAsyncIoConcern()
    {
        var opportunities = new[]
        {
            new ReviewOpportunityItem(
                "Auth/Helpers/AsyncLazy.cs",
                "AsyncLazy",
                "Проверить Task.Factory.StartNew вокруг async I/O фабрики",
                "Фабрика может запускать token refresh/HTTP I/O, а не CPU-bound работу, поэтому стоит убрать thread-pool offload и явно наблюдать ошибки фонового refresh.",
                "Оставить асинхронный путь асинхронным или добавить контролируемую обработку fire-and-forget задачи.",
                11)
        };

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities(opportunities);

        Assert.Single(result);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_KeepsBusinessRuleDuplicationOpportunity()
    {
        var opportunity = new ReviewOpportunityItem(
            "Domain/Extensions/RegionCacheExtensions.cs",
            "EnrichWithRegionData overloads",
            "Дублирование логики обогащения в трёх перегрузках EnrichWithRegionData",
            "Три метода повторяют lookup региона и заполнение user-visible read model полей. При изменении business-rule нужно править несколько копий.",
            "Вынести общий enrichment contract или generic helper.",
            114);

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities([opportunity]);

        Assert.Single(result);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_RemovesFutureRecordEqualityOnlyOpportunity()
    {
        var opportunity = new ReviewOpportunityItem(
            "Domain/ReadModels/RegionCacheItem.cs",
            "RegionCacheItem",
            "RegionCacheItem не переопределяет Equals/GetHashCode для будущего HashSet",
            "Сейчас корректность группировки не страдает, но в будущем reference equality может дать неожиданные результаты при использовании как ключа словаря.",
            "Преобразовать класс в record.",
            1);

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities([opportunity]);

        Assert.Empty(result);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_RemovesReviewPolishNoise()
    {
        var opportunities = new[]
        {
            new ReviewOpportunityItem(
                "Auth/HttpClientsAuthHandler.cs",
                "SendAsync",
                "Безопасное добавление заголовка CorrelationId",
                "Использование TryAddWithoutValidation предотвратит неожиданные исключения при конфликте заголовков.",
                "Заменить request.Headers.Add на TryAddWithoutValidation.",
                31),
            new ReviewOpportunityItem(
                "Auth/HttpEsbClientsAuthHandler.cs",
                "constructor",
                "Использование IOptionsSnapshot для поддержки горячей перезагрузки конфигурации",
                "IOptions.Value фиксирует настройки на момент создания обработчика.",
                "Заменить IOptions на IOptionsSnapshot.",
                14),
            new ReviewOpportunityItem(
                "Auth/BaseServiceTokenService.cs",
                "GetToken",
                "Добавить ConfigureAwait(false) для библиотечного кода",
                "Await без ConfigureAwait(false) может привести к захвату контекста синхронизации.",
                "Добавить ConfigureAwait(false).",
                29),
            new ReviewOpportunityItem(
                "Auth/EsbTokenService.cs",
                "_semaphore",
                "Реализовать IDisposable для освобождения SemaphoreSlim",
                "SemaphoreSlim не освобождается при завершении работы сервиса.",
                "Добавить Dispose и вызвать _semaphore.Dispose().",
                18),
            new ReviewOpportunityItem(
                "Auth/BaseServiceTokenService.cs",
                "GetNotExpired",
                "Вынести создание AsyncLazy в отдельный метод",
                "Создание AsyncLazy дублируется в двух местах.",
                "Создать вспомогательный метод ResetTokenLazy().",
                57),
            new ReviewOpportunityItem(
                "Auth/Models/IdentityTokenModel.cs",
                "IdentityTokenModel",
                "Улучшить поддержку nullable-аннотаций для модели токена",
                "Модель не содержит явных nullable-аннотаций, что затрудняет статический анализ.",
                "Активировать <Nullable>enable</Nullable>.",
                5),
            new ReviewOpportunityItem(
                "Auth/Options/EsbClientAuthOptions.cs",
                "EsbClientAuthOptions",
                "Добавить валидацию опций на старте приложения",
                "Обязательные ключи секции Auth:Esb могут отсутствовать.",
                "Добавить ValidateOnStart для опций.",
                9)
        };

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities(opportunities);

        var opportunity = Assert.Single(result);
        Assert.Contains("валидацию опций", opportunity.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuppressLowSignalOpportunities_RemovesLatestAuthReviewNoise()
    {
        var opportunities = new[]
        {
            new ReviewOpportunityItem(
                "Auth/Abstractions/IIdentityServiceTokenService.cs",
                "interface IIdentityServiceTokenService",
                "Пустой интерфейс без специфичных членов",
                "Интерфейс наследует IServiceTokenService, но не добавляет новых методов или свойств.",
                "Добавить специфичный метод или рассмотреть объединение.",
                4),
            new ReviewOpportunityItem(
                "Auth/HttpClientsAuthHandler.cs",
                "Поле delta",
                "Интервал опережения для обновления токена сделать конфигурируемым",
                "Хардкод TimeSpan.FromSeconds(60) снижает гибкость настройки.",
                "Добавить класс опций TokenRefreshBuffer.",
                11),
            new ReviewOpportunityItem(
                "Auth/HttpClientsServiceOnlyAuthHandler.cs",
                "AuthorizeUsingServiceUserAsync",
                "Обработка отсутствия сервисного токена",
                "При null токене запрос будет отправлен без заголовка авторизации.",
                "Добавить логирование или выброс исключения.",
                40),
            new ReviewOpportunityItem(
                "Auth/HttpEsbClientsAuthHandler.cs",
                "SendAsync",
                "Добавить проверку токена на null",
                "AuthenticationHeaderValue из null вызовет ArgumentNullException.",
                "Проверить accessToken на null.",
                30),
            new ReviewOpportunityItem(
                "Auth/HttpEsbClientsAuthHandler.cs",
                "SendAsync",
                "Кэширование токена доступа",
                "На каждый HTTP-запрос вызывается GetAccessTokenAsync.",
                "Внедрить кэширование токена внутри обработчика.",
                30),
            new ReviewOpportunityItem(
                "Auth/IdentityServiceTokenService.cs",
                "RequestToken",
                "Жёстко заданный путь токен-эндпоинта",
                "URL-путь /connect/token жёстко зашит в коде.",
                "Вынести путь в IdentityClientOptions.",
                42),
            new ReviewOpportunityItem(
                "Auth/IdentityServiceTokenService.cs",
                "RequestToken",
                "Создание HttpClient без предварительной настройки BaseAddress",
                "Можно зарегистрировать именованный HttpClient с BaseAddress и Polly.",
                "Зарегистрировать именованный HttpClient в DI.",
                43),
            new ReviewOpportunityItem(
                "Auth/BaseServiceTokenService.cs",
                "GetNotExpired",
                "Вынести пороговые интервалы в константы или конфигурацию",
                "Значения 1 и 15 минут зашиты в коде.",
                "Определить константы ImmediateRefreshThreshold.",
                54),
            new ReviewOpportunityItem(
                "Auth/BaseServiceTokenService.cs",
                "GetNotExpired",
                "Упростить сравнение времени с помощью операторов сравнения DateTime",
                "Использование AddMinutes и < понятно, но можно улучшить читаемость.",
                "Применить DateTime.UtcNow + TimeSpan.FromMinutes(1) > ValidTo.",
                54),
            new ReviewOpportunityItem(
                "Auth/BaseServiceTokenService.cs",
                "TokenValueFactory",
                "Обработать ошибку парсинга JwtSecurityToken",
                "Конструктор JwtSecurityToken может выбросить исключение при некорректном формате токена.",
                "Обернуть создание JwtSecurityToken в try/catch и залогировать ошибку.",
                45)
        };

        var result = ReviewRunExecutor.SuppressLowSignalOpportunities(opportunities);

        var opportunity = Assert.Single(result);
        Assert.Contains("JwtSecurityToken", opportunity.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_DeduplicatesTaskFactoryStartNewAsyncIoVariants()
    {
        var normalizedFindings = new[]
        {
            CreateFinding(
                "Auth/Helpers/AsyncLazy.cs",
                13,
                FindingCategory.Performance,
                FindingSeverity.Medium,
                "Избыточное создание потоков для асинхронных операций",
                "Конструктор Func<Task<T>> вызывает Task.Factory.StartNew с Unwrap. В контексте получения токенов это ведёт к бесполезной трате ресурсов: асинхронный запрос и так неблокирующий.",
                "base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap())"),
            CreateFinding(
                "Auth/Helpers/AsyncLazy.cs",
                10,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Task.Factory.StartNew используется для async I/O refresh токена",
                "StartNew используется для async factory token refresh через HTTP; это не CPU-bound offload, а вместе с fire-and-forget refresh может потерять cancellation и unobserved exception.",
                "base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap())")
        };

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings([], normalizedFindings);

        var finding = Assert.Single(result);
        Assert.Contains("async I/O", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FindingCategory.Reliability, finding.Category);
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_DeduplicatesSameNullabilityContractAcrossInterfaceAndImplementation()
    {
        var normalizedFindings = new[]
        {
            CreateFinding(
                "Domain/Abstractions/IRegionCacheRepository.cs",
                17,
                FindingCategory.Bug,
                FindingSeverity.Medium,
                "Нарушение контракта nullability в методах репозитория",
                "Методы GetRegionName и GetMacroRegionName в интерфейсе IRegionCacheRepository объявлены как non-nullable string, однако реализация возвращает null.",
                "string GetRegionName(string regionCode);"),
            CreateFinding(
                "Infrastructure/Repositories/RegionCacheRepository.cs",
                40,
                FindingCategory.Bug,
                FindingSeverity.Medium,
                "Нарушение контракта nullability в методах репозитория",
                "Методы GetRegionName и GetMacroRegionName объявлены как non-nullable string, но при отсутствии региона возвращают null.",
                "public string GetRegionName(string regionCode) => _regions.TryGetValue(regionCode, out var region) ? region.Name : null;")
        };

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings([], normalizedFindings);

        var finding = Assert.Single(result);
        Assert.Equal("Infrastructure/Repositories/RegionCacheRepository.cs", finding.File);
        Assert.Contains("null", finding.ExistingCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_KeepsOptionsBindingAndValidationAsDistinctFailureModes()
    {
        var normalizedFindings = new[]
        {
            CreateFinding(
                "Api/DI/AddDependencies.cs",
                104,
                FindingCategory.Reliability,
                FindingSeverity.High,
                "Несоответствие имени секции конфигурации CacheOptions",
                "DI читает CacheOptions через GetSection, но в конфиге ключ лежит в другой секции.",
                "services.Configure<CacheOptions>(configuration.GetSection(\"CacheOptions\"));"),
            CreateFinding(
                "Api/DI/AddDependencies.cs",
                104,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "Options регистрируются без startup-валидации",
                "Options не вызывают ValidateOnStart, поэтому пустая или неправильная секция обнаружится только в runtime.",
                "services.Configure<CacheOptions>(configuration.GetSection(\"CacheOptions\"));")
        };

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings([], normalizedFindings);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, finding => finding.Title.Contains("секции", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, finding => finding.Title.Contains("валидац", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_DeduplicatesSameSqlBusinessFilterIssueAcrossQueries()
    {
        var normalizedFindings = new[]
        {
            CreateFinding(
                "Infrastructure/Db/GetOrderList.sql",
                64,
                FindingCategory.Logic,
                FindingSeverity.High,
                "Удалены критические бизнес-фильтры IsBasic и IsBcAllowed из запросов",
                "Запрос больше не ограничивает регионы по IsBasic и IsBcAllowed.",
                "where rb.\"IsBasic\" is true and rb.\"IsBcAllowed\" is true"),
            CreateFinding(
                "Infrastructure/Db/GetOrderListLiteV2.sql",
                60,
                FindingCategory.Logic,
                FindingSeverity.Medium,
                "SQL перестал фильтровать разрешённые регионы",
                "В другом SQL-запросе также удалены предикаты IsBasic/IsBcAllowed.",
                "where rb.\"IsBasic\" is true and rb.\"IsBcAllowed\" is true")
        };

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings([], normalizedFindings);

        var finding = Assert.Single(result);
        Assert.Contains("IsBasic", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_PrefersExternalAgentWordingForSameRootCause()
    {
        var agentFinding = CreateFinding(
            "Infrastructure/Repositories/RegionCacheRepository.cs",
            634,
            FindingCategory.Bug,
            FindingSeverity.Medium,
            "Недетерминированная загрузка кэша: GroupBy + First() без OrderBy",
            "Агент проверил контекст и объяснил, что при нескольких ReplicBranch на один RegionIsoCode кэш может выбрать разные RegionName/TimeZone/MacroRegionName.",
            "var newCache = regions.GroupBy(r => r.RegionCode).ToFrozenDictionary(g => g.Key, g => g.First());",
            ReviewFindingSource.ExternalReview);
        var deterministicFinding = CreateFinding(
            "Infrastructure/Repositories/RegionCacheRepository.cs",
            634,
            FindingCategory.Logic,
            FindingSeverity.Medium,
            "GroupBy выбирает первый регион недетерминированно",
            "Код группирует записи и берёт First() без явного порядка.",
            ".GroupBy(r => r.RegionCode) | g.First()");

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings([], [agentFinding, deterministicFinding]);

        var finding = Assert.Single(result);
        Assert.Equal(ReviewFindingSource.ExternalReview, finding.Source);
        Assert.Contains("Агент проверил контекст", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreDroppedDistinctFindings_DeduplicatesGroupByEvenWhenSuggestionMentionsBusinessFilters()
    {
        var normalizedFindings = new[]
        {
            CreateFinding(
                "Infrastructure/Repositories/RegionCacheRepository.cs",
                107,
                FindingCategory.Reliability,
                FindingSeverity.Medium,
                "GroupBy + First() без упорядочивания при дубликатах кодов регионов",
                "Кэш строится через GroupBy(r => r.RegionCode), а затем берётся g.First().",
                "var newCache = regions.GroupBy(r => r.RegionCode).ToFrozenDictionary(g => g.Key, g => g.First());"),
            CreateFinding(
                "Infrastructure/Repositories/RegionCacheRepository.cs",
                107,
                FindingCategory.Logic,
                FindingSeverity.Medium,
                "GroupBy выбирает первый регион недетерминированно",
                "Код группирует записи и берёт First() без явного порядка.",
                ".GroupBy(r => r.RegionCode) | g.First()")
        };

        var originalFindings = new[]
        {
            normalizedFindings[1] with
            {
                Suggestion = "Выбрать каноническую запись, например предпочитать IsBasic/IsBcAllowed, и покрыть дубль по RegionCode тестом."
            }
        };

        var result = ReviewRunExecutor.RestoreDroppedDistinctFindings(originalFindings, normalizedFindings);

        Assert.Single(result);
    }

    [Fact]
    public void ApplyFindingEvidenceGate_KeepsFindingAnchoredToChangedHunkLine()
    {
        var finding = CreateFinding(
            "Auth/BaseServiceTokenService.cs",
            42,
            FindingCategory.Security,
            FindingSeverity.High,
            "Refresh identity-токена сравнивает UTC JWT с локальным временем",
            "ValidTo у JWT в UTC, а код сравнивает его с DateTime.Now.",
            "if (_tokenObject.ValidTo < DateTime.Now.AddMinutes(1))");
        var preprocessed = CreatePreprocessedDiff("""
            diff --git a/Auth/BaseServiceTokenService.cs b/Auth/BaseServiceTokenService.cs
            index 1111111..2222222 100644
            --- a/Auth/BaseServiceTokenService.cs
            +++ b/Auth/BaseServiceTokenService.cs
            @@ -40,6 +40,7 @@ public string GetToken()
                 if (_tokenObject.ValidTo < DateTime.Now.AddMinutes(1))
                 {
            +        _tokenLazy = CreateTokenLazy();
                 }
            """);

        var result = ReviewRunExecutor.ApplyFindingEvidenceGate([finding], preprocessed);

        Assert.Single(result);
    }

    [Fact]
    public void ApplyFindingEvidenceGate_KeepsFindingAnchoredByExistingCodeWhenLineIsMissing()
    {
        var finding = CreateFinding(
            "Auth/BaseServiceTokenService.cs",
            0,
            FindingCategory.Security,
            FindingSeverity.High,
            "Refresh identity-токена сравнивает UTC JWT с локальным временем",
            "ValidTo у JWT в UTC, а код сравнивает его с DateTime.Now.",
            "if (_tokenObject.ValidTo < DateTime.Now.AddMinutes(1))");
        var preprocessed = CreatePreprocessedDiff("""
            diff --git a/Auth/BaseServiceTokenService.cs b/Auth/BaseServiceTokenService.cs
            index 1111111..2222222 100644
            --- a/Auth/BaseServiceTokenService.cs
            +++ b/Auth/BaseServiceTokenService.cs
            @@ -40,6 +40,7 @@ public string GetToken()
                 if (_tokenObject.ValidTo < DateTime.Now.AddMinutes(1))
                 {
            +        _tokenLazy = CreateTokenLazy();
                 }
            """);

        var result = ReviewRunExecutor.ApplyFindingEvidenceGate([finding], preprocessed);

        Assert.Single(result);
    }

    [Fact]
    public void ApplyFindingEvidenceGate_RemovesFindingOutsideChangedFiles()
    {
        var finding = CreateFinding(
            "Auth/OtherService.cs",
            42,
            FindingCategory.Reliability,
            FindingSeverity.Medium,
            "Модельное замечание без diff-якоря",
            "Файл не участвует в diff, поэтому замечание нельзя проверить по этому PR.",
            "return cachedToken;");
        var preprocessed = CreatePreprocessedDiff("""
            diff --git a/Auth/BaseServiceTokenService.cs b/Auth/BaseServiceTokenService.cs
            index 1111111..2222222 100644
            --- a/Auth/BaseServiceTokenService.cs
            +++ b/Auth/BaseServiceTokenService.cs
            @@ -40,6 +40,7 @@ public string GetToken()
            +    return token;
            """);

        var result = ReviewRunExecutor.ApplyFindingEvidenceGate([finding], preprocessed);

        Assert.Empty(result);
    }

    [Fact]
    public void ApplyFindingEvidenceGate_RemovesUnknownFindingOnChangedFileWithoutLineOrCodeEvidence()
    {
        var finding = CreateFinding(
            "Auth/BaseServiceTokenService.cs",
            0,
            FindingCategory.Architecture,
            FindingSeverity.Medium,
            "Сервис можно сделать чище",
            "Общее замечание не указывает строку, код или проверяемый риск из diff.",
            "");
        var preprocessed = CreatePreprocessedDiff("""
            diff --git a/Auth/BaseServiceTokenService.cs b/Auth/BaseServiceTokenService.cs
            index 1111111..2222222 100644
            --- a/Auth/BaseServiceTokenService.cs
            +++ b/Auth/BaseServiceTokenService.cs
            @@ -40,6 +40,7 @@ public string GetToken()
            +    return token;
            """);

        var result = ReviewRunExecutor.ApplyFindingEvidenceGate([finding], preprocessed);

        Assert.Empty(result);
    }

    [Fact]
    public void ApplyFindingEvidenceGate_KeepsSqlJoinAndGroupByFindingsAnchoredToDiff()
    {
        var joinFinding = CreateFinding(
            "Infrastructure/Db/GetOrderListForExcelFile.sql",
            41,
            FindingCategory.Bug,
            FindingSeverity.High,
            "Мёртвый LEFT JOIN ReplicBranch вызывает дублирование строк",
            "JOIN больше не поставляет данные в SELECT, но может размножить строки при дублях RegionIsoCode.",
            "left join \"ReplicBranch\" rb\n          on o.\"RegionCode\" = rb.\"RegionIsoCode\"");
        var groupByFinding = CreateFinding(
            "Infrastructure/Repositories/RegionCacheRepository.cs",
            634,
            FindingCategory.Bug,
            FindingSeverity.High,
            "Недетерминированная загрузка кэша: GroupBy + First() без OrderBy",
            "GroupBy по RegionCode берёт g.First() без явной сортировки.",
            "GroupBy(r => r.RegionCode, StringComparer.OrdinalIgnoreCase)");
        var preprocessed = CreatePreprocessedDiff("""
            diff --git a/Infrastructure/Db/GetOrderListForExcelFile.sql b/Infrastructure/Db/GetOrderListForExcelFile.sql
            index 1111111..2222222 100644
            --- a/Infrastructure/Db/GetOrderListForExcelFile.sql
            +++ b/Infrastructure/Db/GetOrderListForExcelFile.sql
            @@ -39,8 +39,6 @@ from "Order" o
                      left join "ReplicBranch" rb
                                on o."RegionCode" = rb."RegionIsoCode"
            -where rb."IsBasic" is true
            -  and rb."IsBcAllowed" is true
            +where (@orderId is null or o."OrderId" = @orderId)
            diff --git a/Infrastructure/Repositories/RegionCacheRepository.cs b/Infrastructure/Repositories/RegionCacheRepository.cs
            new file mode 100644
            index 0000000..3333333
            --- /dev/null
            +++ b/Infrastructure/Repositories/RegionCacheRepository.cs
            @@ -0,0 +630,12 @@
            +            var newCache = regions
            +                .GroupBy(r => r.RegionCode, StringComparer.OrdinalIgnoreCase)
            +                .ToFrozenDictionary(
            +                    g => g.Key,
            +                    g => g.First(),
            +                    StringComparer.OrdinalIgnoreCase);
            """);

        var result = ReviewRunExecutor.ApplyFindingEvidenceGate([joinFinding, groupByFinding], preprocessed);

        var titles = string.Join(" | ", result.Select(finding => finding.Title));
        Assert.True(result.Any(finding => finding.Title.Contains("JOIN", StringComparison.OrdinalIgnoreCase)), titles);
        Assert.True(result.Any(finding => finding.Title.Contains("GroupBy", StringComparison.OrdinalIgnoreCase)), titles);
    }

    [Fact]
    public void SuppressContradictedByDiffFindings_RemovesSingletonWarningWhenDiffRegistersSingleton()
    {
        var finding = CreateFinding(
            "Auth/ServiceToken/EsbTokenService.cs",
            13,
            FindingCategory.Architecture,
            FindingSeverity.Medium,
            "Требуется регистрация как Singleton",
            "Сервис кэширует токен. Если регистрация будет Scoped или Transient, каждый экземпляр будет иметь собственный кэш.",
            "public class EsbTokenService : IEsbTokenService");
        var diff = """
            diff --git a/Auth/Extensions/ServiceCollectionAuthExtensions.cs b/Auth/Extensions/ServiceCollectionAuthExtensions.cs
            index 1111111..2222222 100644
            --- a/Auth/Extensions/ServiceCollectionAuthExtensions.cs
            +++ b/Auth/Extensions/ServiceCollectionAuthExtensions.cs
            @@ -20,6 +20,7 @@ public static IServiceCollection AddEsbClientsAuth(...)
            +    services.AddSingleton<IEsbTokenService, EsbTokenService>();
            +    services.AddTransient<HttpEsbClientsAuthHandler>();
            """;

        var result = ReviewRunExecutor.SuppressContradictedByDiffFindings([finding], diff);

        Assert.Empty(result);
    }

    private static ReviewFinding CreateFinding(
        string file,
        int line,
        FindingCategory category,
        FindingSeverity severity,
        string title,
        string description,
        string existingCode,
        ReviewFindingSource source = ReviewFindingSource.InitialReview)
    {
        return new ReviewFinding(
            file,
            $"line {line}",
            category,
            severity,
            source,
            title,
            description,
            existingCode,
            "Исправить контракт или запрос так, чтобы поведение соответствовало объявленному сценарию.",
            line,
            line);
    }

    private static PreprocessedDiff CreatePreprocessedDiff(string diff)
    {
        return new PreprocessedDiff
        {
            FilteredDiffText = diff,
            ReviewContextDiffText = diff,
            ChangedFiles = ["Auth/BaseServiceTokenService.cs"],
            ReviewChunks = [diff],
            Chunks = [diff]
        };
    }
}
