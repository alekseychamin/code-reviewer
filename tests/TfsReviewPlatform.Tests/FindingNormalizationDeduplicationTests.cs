using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class FindingNormalizationDeduplicationTests
{
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
    public void RestoreDroppedDistinctFindings_KeepsSameSqlBusinessFilterIssueInDifferentQueries()
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

        Assert.Equal(2, result.Count);
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

    private static ReviewFinding CreateFinding(
        string file,
        int line,
        FindingCategory category,
        FindingSeverity severity,
        string title,
        string description,
        string existingCode)
    {
        return new ReviewFinding(
            file,
            $"line {line}",
            category,
            severity,
            ReviewFindingSource.InitialReview,
            title,
            description,
            existingCode,
            "Исправить контракт или запрос так, чтобы поведение соответствовало объявленному сценарию.",
            line,
            line);
    }
}
