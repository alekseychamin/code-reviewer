using System.Text.RegularExpressions;
using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Application.Services;

public sealed class FindingsNormalizer : IFindingsNormalizer
{
    private static readonly Regex SelfDismissedFindingRegex = new(
        @"\b(no defect|no issue|no problem|nothing to fix|false positive|this is correct|this is valid|is likely correct|likely correct|which is fine|works correctly|fix is not needed)\b|(?:не дефект|нет проблемы|ошибки нет|всё корректно|все корректно|это корректно|исправление не требуется|ничего исправлять не нужно)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SelfContradictionRegex = new(
        @"(?:not an error|not actually an error|may be expected behavior|might be expected behavior|check the contract|if this behavior is intended|if intended|leave as is|can be left as is|стоит проверить контракт|может быть ожидаемым поведением|может быть корректным поведением|это не является ошибкой|если это корректно|если текущее поведение корректно|можно оставить как есть|оставить как есть)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CleanupNoiseRegex = new(
        @"(?:null object|emptyifnull|split test|разделить тест|вынести в констант|дублировани[ея] строк|строковых литерал|helper|хелпер|упростить тестов|тестовый репозиторий можно|создать приватный метод|вынести проверку|дублирование логики|косметическ|readability|читаемост|поддержк[аи] при изменении автора)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RuntimeReloadSpeculationRegex = new(
        @"(?:ioptionsmonitor|optionsmonitor|runtime config|runtime reload|hot reload|reactive update|reactive configuration|изменит(?:ся|ься) во время работы|во время работы приложения|без перезапуска|динамическ(?:ое|ая) обновлен|горяч(?:ая|ее) перезагрузк|считывается один раз|read once|captured once)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExplicitUnusedConfigRegex = new(
        @"(?:не использ|unused|read but not used|stored but not used|captured but not used|логируется, но не влияет|не влияет на поведение|does not affect behavior|does not materially affect behavior)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TestLifecycleSpeculationRegex = new(
        @"(?:stopasync|cancelasync|executetask|порядок остановки|небезопасн(?:ая|ый) остановк|утечк[аи] задач|неопределенн(?:ое|ый) состояни)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExplicitLifecycleFailureEvidenceRegex = new(
        @"(?:hang|hung|deadlock|stuck|never completes|never complete|swallowed exception|unobserved|flaky|timing-dependent|ложн(?:ое|ые) срабатывани|зависани|утечк[аи] ресурс|leak(ed)? background work)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DisposableLeakSpeculationRegex = new(
        @"(?:cancellationtokensource|cts|dispose|disposable|утечк[аи] .*cancellationtokensource|утечк[аи] ресурс|leak)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VisibleUsingDisposeEvidenceRegex = new(
        @"(?:using var|await using|созда[её]тся .*using|wrapped in using)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InMemoryMicroOptimizationRegex = new(
        @"(?:batch[- ]?lookup|batch-запрос|one pass|single pass|один проход|trygetvalue|нескольк(?:о|их) обращени[йя] к словар|separate dictionary lookups|frozendictionary|dictionary lookup|поиск[аи] в словар|извлекая данные за один вызов)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DefensiveNullCheckNoiseRegex = new(
        @"(?:argumentnullexception\.throwifnull|throwifnull|defensive null check|защитн(?:ая|ые) проверк[аи] null|nullreferenceexception|regioncacherepository == null|regioncacherepository is null)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SyntaxOnlyNullCheckRewriteRegex = new(
        @"(?:заменить .*== null.*is null|switch .*== null.*is null|использовать is null|for consistency|для согласованности|современн(?:ые|ой) практик[аи] c#)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConfigurabilityNoiseRegex = new(
        @"(?:move constants? to config|вынести .* в конфигурац|сделать .* настраиваем|make .* configurable|добавить .* в cacheoptions|option validation|валид(?:аци|ир) .*config|валид(?:аци|ир) .*options)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EfMappingSpeculationRegex = new(
        @"(?:notmapped|\[notmapped\]|entity framework|ef core|orm|маппинг|mapped to db|маппить .* на столбец)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NullableContractMatchRegex = new(
        @"(?:declared as returning string\?|объявлен[ао]? как .*string\?|сигнатур[ае].*string\?|возвращающ(?:ий|ая) string\?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExceptionHardeningSpeculationRegex = new(
        @"(?:try-catch|обработк[аи] исключени|игнорировани[ея] исключени|часть заказов останется без данных|one item.*not affect|ошибка для одного .* не влияла)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CacheFallbackArchitectureSpeculationRegex = new(
        @"(?:удал[её]н join|removed join|left join .*replicbranch|данные о регионе .* больше не будут извлекаться|кэш пуст|cache empty|fallback-логик|fallback logic|фоновый сервис .* кэширован|background service .* cache)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NullCoalescingOverwriteSpeculationRegex = new(
        @"(?:\?\?=|null-coalescing assignment|оператор \?\?=|перезапиш\w* на null|стере\w* ранее установленн\w* данн\w*|overwrite.*null|erase.*existing.*data)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RegionCodeOnlyEnrichmentSpeculationRegex = new(
        @"(?:orderregioncode|regioncode|код региона).*(?:orderregionname|macroregionname|имя региона|название региона|макрорегион)|(?:orderregionname|macroregionname|имя региона|название региона|макрорегион).*(?:orderregioncode|regioncode|код региона)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NullableDtoPropertySpeculationRegex = new(
        @"(?:не-nullable свойств|non-nullable propert|может содержать null|can contain null|кэш .* может быть пуст|cache .* may be empty|не содержит данных для конкретного региона|does not contain data for a specific region|заполняют(?:ся|ся) из кэша|filled from cache|notmapped|не инициализирован|not initialized|не будет заполняться из базы данных|will not be populated from the database)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TestAssertionDoubtRegex = new(
        @"(?:should\(\)\.be\(null\)|ожидается null|expected null|без явного обоснования|нужно убедиться, что это ожидаемое поведение|may mask a real error|может маскировать реальную ошибку)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TimeZoneTypeSpeculationRegex = new(
        @"(?:timezoneinfo|iana|tzdb|europe/moscow|смещение в часах|offset hours|строков\w* идентификатор\w* часового пояса|числов\w* идентификатор\w* часового пояса|историческ\w* изменени\w*|летнее время)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CacheReadinessSpeculationRegex = new(
        @"(?:isloaded|cache .* not loaded|кэш .* не загружен|перед вызовом обогащени|before enrichment|проверк\w* isloaded|cache state|state of the cache|best-effort|отложенн\w* загрузк\w* кэша|явн\w* проверк\w* состояния кэша)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NullabilitySignalRegex = new(
        @"(?:nullable|nullability|non-nullable|nonnullable|string\?|string \?|возвращает null|return null|может вернуть null|контракт nullable|контракт nullability)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OperationalConfigSignalRegex = new(
        @"(?:ttl|timeout|retry|refresh interval|refresh|cachettl|интервал|таймаут|ttl|ретра|повторн|конфигурац|настройк|не использ|unused)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeterminismSignalRegex = new(
        @"(?:groupby|first\(\)|single\(\)|todictionary|tofrozendictionary|дубликат|duplicate|ключ|недетерминирован|потер[яи] данных|silent data loss)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FlakyTestSignalRegex = new(
        @"(?:task\.delay|waitasync|manualresetevent|флак|flaky|timing|тайминг|polling|поллинг|ожидани[ея])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] HighSignalOpportunityKeywords =
    [
        "ttl", "timeout", "retry", "refresh", "cache", "isloaded", "task.delay", "polling", "nullability",
        "non-nullable", "string?", "contract", "groupby", "first()", "duplicate", "дубликат", "таймаут", "ttl"
    ];

    public IReadOnlyList<ReviewFinding> Normalize(IEnumerable<string> rawResponses)
    {
        var findings = rawResponses
            .SelectMany(ParseFindingsResponse)
            .GroupBy(item => $"{item.File}|{item.Title[..Math.Min(item.Title.Length, 24)]}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Description.Length)
                .First())
            .OrderBy(item => item.Severity)
            .ToArray();

        return findings;
    }

    public ChunkReviewNormalizationResult NormalizeChunkReview(IEnumerable<string> rawResponses)
    {
        var parsed = rawResponses
            .Select(ParseChunkReviewResponse)
            .ToArray();

        var findings = DeduplicateFindingsCrossPass(parsed.SelectMany(item => item.Findings));
        var opportunities = DeduplicateOpportunitiesCrossPass(parsed.SelectMany(item => item.Opportunities));
        opportunities = SuppressOpportunitiesCoveredByFindings(opportunities, findings);
        opportunities = ApplyPerFileOpportunityCap(opportunities, 2);

        return new ChunkReviewNormalizationResult(findings, opportunities);
    }

    private static IReadOnlyList<ReviewFinding> ParseFindingsResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var payload = raw.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
        {
            payload = payload.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray(),
                JsonValueKind.Object when document.RootElement.TryGetProperty("findings", out var findings) =>
                    findings.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray(),
                JsonValueKind.Object => MapFinding(document.RootElement) is { } finding ? [finding] : [],
                _ => []
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ChunkReviewNormalizationResult ParseChunkReviewResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ChunkReviewNormalizationResult([], []);
        }

        var payload = raw.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
        {
            payload = payload.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return new ChunkReviewNormalizationResult(
                    root.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray(),
                    []);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var findings = root.TryGetProperty("findings", out var findingsNode) && findingsNode.ValueKind == JsonValueKind.Array
                    ? findingsNode.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray()
                    : MapFinding(root) is { } singleFinding ? [singleFinding] : [];

                var opportunities = root.TryGetProperty("opportunities", out var opportunitiesNode) && opportunitiesNode.ValueKind == JsonValueKind.Array
                    ? opportunitiesNode.EnumerateArray().Select(MapOpportunity).Where(item => item is not null).Cast<ReviewOpportunityItem>().ToArray()
                    : [];

                return new ChunkReviewNormalizationResult(findings, opportunities);
            }
        }
        catch (JsonException)
        {
        }

        return new ChunkReviewNormalizationResult([], []);
    }

    private static ReviewFinding? MapFinding(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = ReadString(element, "file");
        var title = ReadString(element, "title");
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var kind = ReadString(element, "kind");
        var finding = new ReviewFinding(
            file,
            ReadString(element, "line_hint") ?? ReadString(element, "location") ?? "Unknown",
            ParseCategory(ReadString(element, "type")),
            ParseSeverity(ReadString(element, "severity")),
            ReviewFindingSource.InitialReview,
            title,
            ReadString(element, "description") ?? ReadString(element, "problem") ?? string.Empty,
            ReadString(element, "existing_code") ?? ReadString(element, "bad_code") ?? string.Empty,
            ReadString(element, "suggestion") ?? ReadString(element, "fix") ?? string.Empty,
            ReadInt(element, "start_line"),
            ReadInt(element, "end_line"),
            Guid.NewGuid());

        return ShouldKeepFinding(finding, kind) ? finding : null;
    }

    private static ReviewOpportunityItem? MapOpportunity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = ReadString(element, "file");
        var title = ReadString(element, "title");
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var description = ReadString(element, "description")?.Trim() ?? string.Empty;
        var suggestion = ReadString(element, "suggestion")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(suggestion))
        {
            return null;
        }

        return new ReviewOpportunityItem(
            file.Trim(),
            ReadString(element, "line_hint")?.Trim() ?? string.Empty,
            title.Trim(),
            description,
            suggestion,
            ReadInt(element, "start_line"));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static FindingSeverity ParseSeverity(string? severity)
    {
        return Enum.TryParse<FindingSeverity>(severity, true, out var parsed)
            ? parsed
            : FindingSeverity.Medium;
    }

    private static FindingCategory ParseCategory(string? category)
    {
        return Enum.TryParse<FindingCategory>(category, true, out var parsed)
            ? parsed
            : FindingCategory.Bug;
    }

    private static bool ShouldKeepFinding(ReviewFinding finding, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind) &&
            !kind.Equals("Defect", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("Risk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (finding.Category == FindingCategory.CodeStyle)
        {
            return false;
        }

        if (LooksSelfDismissed(finding))
        {
            return false;
        }

        if (LooksSelfContradictory(finding))
        {
            return false;
        }

        if (LooksLikeRuntimeReloadSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeTestLifecycleSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeDisposableLeakSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeEfMappingSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeNullableContractMatch(finding))
        {
            return false;
        }

        if (LooksLikeExceptionHardeningSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeDefensiveNullCheckFindingNoise(finding))
        {
            return false;
        }

        if (LooksLikeCacheFallbackArchitectureSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeNullCoalescingOverwriteSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeRegionCodeOnlyEnrichmentSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeNullableDtoPropertySpeculation(finding))
        {
            return false;
        }

        if (LooksLikeTestAssertionDoubt(finding))
        {
            return false;
        }

        if (LooksLikeTimeZoneTypeSpeculation(finding))
        {
            return false;
        }

        if (LooksLikeCacheReadinessSpeculation(finding))
        {
            return false;
        }

        return true;
    }

    private static bool LooksSelfDismissed(ReviewFinding finding)
    {
        return SelfDismissedFindingRegex.IsMatch(finding.Description)
               || SelfDismissedFindingRegex.IsMatch(finding.Suggestion);
    }

    private static bool LooksSelfContradictory(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion}";
        return SelfContradictionRegex.IsMatch(text);
    }

    private static bool LooksLikeRuntimeReloadSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!RuntimeReloadSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return !ExplicitUnusedConfigRegex.IsMatch(text);
    }

    private static bool LooksLikeTestLifecycleSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!TestLifecycleSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return !ExplicitLifecycleFailureEvidenceRegex.IsMatch(text);
    }

    private static bool LooksLikeDisposableLeakSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!DisposableLeakSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return VisibleUsingDisposeEvidenceRegex.IsMatch(text);
    }

    private static bool LooksLikeEfMappingSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!EfMappingSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return !text.Contains("[NotMapped]", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("DbSet", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("modelBuilder", StringComparison.OrdinalIgnoreCase)
               && !text.Contains(".Entity<", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNullableContractMatch(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!NullableContractMatchRegex.IsMatch(text))
        {
            return false;
        }

        return NullabilitySignalRegex.IsMatch(text);
    }

    private static bool LooksLikeExceptionHardeningSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!ExceptionHardeningSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return finding.File.Contains("Extensions", StringComparison.OrdinalIgnoreCase) ||
               finding.File.Contains("Provider", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDefensiveNullCheckFindingNoise(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!DefensiveNullCheckNoiseRegex.IsMatch(text))
        {
            return false;
        }

        return finding.File.Contains("Extensions", StringComparison.OrdinalIgnoreCase)
               || finding.File.Contains("Provider", StringComparison.OrdinalIgnoreCase)
               || finding.File.Contains("AddDependencies", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCacheFallbackArchitectureSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!CacheFallbackArchitectureSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return text.Contains("cache", StringComparison.OrdinalIgnoreCase)
               || text.Contains("кэш", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNullCoalescingOverwriteSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!NullCoalescingOverwriteSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return finding.File.Contains("Extensions", StringComparison.OrdinalIgnoreCase)
               || finding.File.Contains("Provider", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRegionCodeOnlyEnrichmentSpeculation(ReviewFinding finding)
    {
        if (!finding.File.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!RegionCodeOnlyEnrichmentSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return text.Contains("cache", StringComparison.OrdinalIgnoreCase)
               || text.Contains("кэш", StringComparison.OrdinalIgnoreCase)
               || text.Contains("enrich", StringComparison.OrdinalIgnoreCase)
               || text.Contains("обогащ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNullableDtoPropertySpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!NullableDtoPropertySpeculationRegex.IsMatch(text))
        {
            return false;
        }

        if (!finding.File.Contains("ReadModel", StringComparison.OrdinalIgnoreCase) &&
            !finding.File.Contains("DbModel", StringComparison.OrdinalIgnoreCase) &&
            !finding.File.Contains("Dto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !text.Contains("dereference", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("ToUpper", StringComparison.OrdinalIgnoreCase)
               && !text.Contains(".Length", StringComparison.OrdinalIgnoreCase)
               && !text.Contains(".Trim(", StringComparison.OrdinalIgnoreCase)
               && !text.Contains(".ToString(", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("throw", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("required", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("обязательно", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("assertion fails", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("используется без проверки", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("used without null check", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTestAssertionDoubt(ReviewFinding finding)
    {
        if (!IsTestFile(finding.File))
        {
            return false;
        }

        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!TestAssertionDoubtRegex.IsMatch(text))
        {
            return false;
        }

        return !text.Contains("contradict", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("противореч", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("seed data directly", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("setup directly", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("assertion fails", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTimeZoneTypeSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!TimeZoneTypeSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        return finding.File.Contains("IRegionCacheRepository", StringComparison.OrdinalIgnoreCase)
               || finding.File.Contains("RegionCacheRepository", StringComparison.OrdinalIgnoreCase)
               || finding.Title.Contains("часов", StringComparison.OrdinalIgnoreCase)
               || finding.Title.Contains("timezone", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCacheReadinessSpeculation(ReviewFinding finding)
    {
        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (!CacheReadinessSpeculationRegex.IsMatch(text))
        {
            return false;
        }

        var isEnrichmentArea =
            finding.File.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            finding.File.Contains("Extensions", StringComparison.OrdinalIgnoreCase);

        if (!isEnrichmentArea)
        {
            return false;
        }

        var mentionsOnlyBestEffort =
            text.Contains("enrichwithregiondata", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("обогащ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cache enrichment", StringComparison.OrdinalIgnoreCase);

        var hasConcreteBrokenContract =
            text.Contains("throws", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("throw", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("nullreferenceexception", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("assertion fails", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("broken contract", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("нарушение контракта", StringComparison.OrdinalIgnoreCase);

        return mentionsOnlyBestEffort && !hasConcreteBrokenContract;
    }

    private static IReadOnlyList<ReviewFinding> DeduplicateFindingsCrossPass(IEnumerable<ReviewFinding> findings)
    {
        var merged = new List<ReviewFinding>();
        foreach (var finding in findings)
        {
            var duplicateIndex = merged.FindIndex(existing => AreLikelyDuplicateFindings(existing, finding));
            if (duplicateIndex < 0)
            {
                merged.Add(finding);
                continue;
            }

            merged[duplicateIndex] = SelectPreferredFinding(merged[duplicateIndex], finding);
        }

        return merged
            .OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartLine > 0 ? item.StartLine : int.MaxValue)
            .ThenByDescending(GetFindingPriorityScore)
            .ToArray();
    }

    private static IReadOnlyList<ReviewOpportunityItem> DeduplicateOpportunitiesCrossPass(IEnumerable<ReviewOpportunityItem> opportunities)
    {
        var filtered = opportunities
            .Where(item => !LooksLikeLowSignalCleanupNoise(item))
            .ToArray();

        var merged = new List<ReviewOpportunityItem>();
        foreach (var opportunity in filtered)
        {
            var duplicateIndex = merged.FindIndex(existing => AreLikelyDuplicateOpportunities(existing, opportunity));
            if (duplicateIndex < 0)
            {
                merged.Add(opportunity);
                continue;
            }

            merged[duplicateIndex] = SelectPreferredOpportunity(merged[duplicateIndex], opportunity);
        }

        return merged
            .OrderByDescending(GetOpportunityPriorityScore)
            .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartLine > 0 ? item.StartLine : int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ReviewOpportunityItem> SuppressOpportunitiesCoveredByFindings(
        IReadOnlyList<ReviewOpportunityItem> opportunities,
        IReadOnlyList<ReviewFinding> findings)
    {
        return opportunities
            .Where(opportunity => findings.All(finding => !CoversSameConcern(finding, opportunity)))
            .ToArray();
    }

    private static IReadOnlyList<ReviewOpportunityItem> ApplyPerFileOpportunityCap(
        IReadOnlyList<ReviewOpportunityItem> opportunities,
        int perFileCap)
    {
        return opportunities
            .GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderByDescending(GetOpportunityPriorityScore)
                .ThenBy(item => item.StartLine > 0 ? item.StartLine : int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(perFileCap))
            .OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartLine > 0 ? item.StartLine : int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AreLikelyDuplicateFindings(ReviewFinding left, ReviewFinding right)
    {
        if (!PathsMatch(left.File, right.File))
        {
            return false;
        }

        if (NormalizeText(left.Title) == NormalizeText(right.Title) &&
            TopicsSimilar($"{left.Title} {left.Description}", $"{right.Title} {right.Description}", 0.5))
        {
            return true;
        }

        if (!LinesClose(left.StartLine, right.StartLine, 3) && !LinesClose(left.EndLine, right.EndLine, 3))
        {
            return false;
        }

        var leftCode = NormalizeText(left.ExistingCode);
        var rightCode = NormalizeText(right.ExistingCode);
        if (!string.IsNullOrWhiteSpace(leftCode) && leftCode == rightCode)
        {
            return true;
        }

        return TopicsSimilar($"{left.Title} {left.Description}", $"{right.Title} {right.Description}", 0.6);
    }

    private static bool AreLikelyDuplicateOpportunities(ReviewOpportunityItem left, ReviewOpportunityItem right)
    {
        if (!PathsMatch(left.File, right.File))
        {
            return false;
        }

        if (!LinesClose(left.StartLine, right.StartLine, 3))
        {
            return false;
        }

        var leftSuggestion = NormalizeText(left.Suggestion);
        var rightSuggestion = NormalizeText(right.Suggestion);
        if (!string.IsNullOrWhiteSpace(leftSuggestion) && leftSuggestion == rightSuggestion)
        {
            return true;
        }

        return TopicsSimilar($"{left.Title} {left.Description}", $"{right.Title} {right.Description}", 0.6);
    }

    private static ReviewFinding SelectPreferredFinding(ReviewFinding left, ReviewFinding right)
    {
        var leftScore = GetFindingPriorityScore(left);
        var rightScore = GetFindingPriorityScore(right);
        if (rightScore != leftScore)
        {
            return rightScore > leftScore ? right : left;
        }

        return (right.Description.Length + right.Suggestion.Length) > (left.Description.Length + left.Suggestion.Length)
            ? right
            : left;
    }

    private static ReviewOpportunityItem SelectPreferredOpportunity(ReviewOpportunityItem left, ReviewOpportunityItem right)
    {
        var leftScore = GetOpportunityPriorityScore(left);
        var rightScore = GetOpportunityPriorityScore(right);
        if (rightScore != leftScore)
        {
            return rightScore > leftScore ? right : left;
        }

        return (right.Description.Length + right.Suggestion.Length + right.Title.Length) >
               (left.Description.Length + left.Suggestion.Length + left.Title.Length)
            ? right
            : left;
    }

    private static bool CoversSameConcern(ReviewFinding finding, ReviewOpportunityItem opportunity)
    {
        if (!PathsMatch(finding.File, opportunity.File))
        {
            return false;
        }

        if (!LinesClose(finding.StartLine, opportunity.StartLine, 4) &&
            !LinesClose(finding.EndLine, opportunity.StartLine, 4))
        {
            return false;
        }

        return TopicsSimilar($"{finding.Title} {finding.Description}", $"{opportunity.Title} {opportunity.Description}", 0.45);
    }

    private static int GetFindingPriorityScore(ReviewFinding finding)
    {
        var score = finding.Severity switch
        {
            FindingSeverity.Critical => 40,
            FindingSeverity.High => 30,
            FindingSeverity.Medium => 20,
            _ => 10
        };

        var text = $"{finding.Title} {finding.Description} {finding.Suggestion} {finding.ExistingCode}";
        if (NullabilitySignalRegex.IsMatch(text))
        {
            score += 15;
        }

        if (OperationalConfigSignalRegex.IsMatch(text))
        {
            score += 10;
        }

        if (DeterminismSignalRegex.IsMatch(text))
        {
            score += 12;
        }

        if (FlakyTestSignalRegex.IsMatch(text))
        {
            score += 8;
        }

        return score;
    }

    private static int GetOpportunityPriorityScore(ReviewOpportunityItem opportunity)
    {
        var score = 0;
        var text = $"{opportunity.Title} {opportunity.Description} {opportunity.Suggestion}";

        if (!string.IsNullOrWhiteSpace(opportunity.Suggestion))
        {
            score += 4;
        }

        score += HighSignalOpportunityKeywords.Count(keyword =>
            text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) * 2;

        if (IsTestFile(opportunity.File))
        {
            score -= 2;
        }

        if (CleanupNoiseRegex.IsMatch(text))
        {
            score -= 8;
        }

        if (InMemoryMicroOptimizationRegex.IsMatch(text))
        {
            score -= 10;
        }

        return score;
    }

    private static bool LooksLikeLowSignalCleanupNoise(ReviewOpportunityItem item)
    {
        var text = $"{item.Title} {item.Description} {item.Suggestion}";
        if (CleanupNoiseRegex.IsMatch(text))
        {
            return true;
        }

        if (LooksLikeDefensiveNullCheckNoise(item, text))
        {
            return true;
        }

        if (LooksLikeConfigurabilityNoise(item, text))
        {
            return true;
        }

        if (InMemoryMicroOptimizationRegex.IsMatch(text) &&
            !OperationalConfigSignalRegex.IsMatch(text) &&
            !NullabilitySignalRegex.IsMatch(text) &&
            !DeterminismSignalRegex.IsMatch(text))
        {
            return true;
        }

        return IsTestFile(item.File) && !FlakyTestSignalRegex.IsMatch(text) && !OperationalConfigSignalRegex.IsMatch(text);
    }

    private static bool LooksLikeDefensiveNullCheckNoise(ReviewOpportunityItem item, string text)
    {
        if (!DefensiveNullCheckNoiseRegex.IsMatch(text) && !SyntaxOnlyNullCheckRewriteRegex.IsMatch(text))
        {
            return false;
        }

        return item.File.Contains("Extensions", StringComparison.OrdinalIgnoreCase)
               || item.File.Contains("Provider", StringComparison.OrdinalIgnoreCase)
               || item.File.Contains("AddDependencies", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeConfigurabilityNoise(ReviewOpportunityItem item, string text)
    {
        if (!ConfigurabilityNoiseRegex.IsMatch(text))
        {
            return false;
        }

        return item.File.Contains("AddDependencies", StringComparison.OrdinalIgnoreCase)
               || item.File.Contains("appsettings", StringComparison.OrdinalIgnoreCase)
               || item.File.Contains("BackgroundService", StringComparison.OrdinalIgnoreCase)
               || item.File.Contains("Options", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsMatch(string left, string right)
    {
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LinesClose(int left, int right, int tolerance)
    {
        if (left <= 0 || right <= 0)
        {
            return false;
        }

        return Math.Abs(left - right) <= tolerance;
    }

    private static bool TopicsSimilar(string left, string right, double threshold)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return NormalizeText(left) == NormalizeText(right);
        }

        var overlap = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var minSize = Math.Min(leftTokens.Length, rightTokens.Length);
        return overlap >= Math.Max(2, (int)Math.Ceiling(minSize * threshold));
    }

    private static string[] Tokenize(string value)
    {
        return NormalizeText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 2)
            .Where(token => token is not ("для" or "при" or "или" or "что" or "это" or "code" or "test" or "tests"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeText(string value)
    {
        return value
            .ToLowerInvariant()
            .Replace("string?", " stringnullable ")
            .Replace("first()", " firstcall ")
            .Replace("task.delay", " taskdelay ")
            .Replace("isloaded", " isloaded ")
            .Replace("tofrozentictionary", " tofrozendictionary ")
            .Replace("tofrozendictionary", " tofrozendictionary ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
    }

    private static bool IsTestFile(string path)
    {
        return path.Contains("test", StringComparison.OrdinalIgnoreCase);
    }
}
