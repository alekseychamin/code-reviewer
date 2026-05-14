using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Tests;

public sealed class ConfirmedDeterministicFindingTests
{
    [Fact]
    public void BuildConfirmedDeterministicFindings_AddsFindings_ForHighConfidenceHints()
    {
        var hints = new[]
        {
            new ReviewHint
            {
                RuleId = "SQL_REMOVED_BUSINESS_FILTER",
                Category = "SQL/DataIntegrity",
                FilePath = "Infrastructure/Db/GetOrderList.sql",
                StartLine = 59,
                Message = "Predicate lines containing IsBasic/IsBcAllowed were removed.",
                Evidence = "where rb.\"IsBasic\" is true | and rb.\"IsBcAllowed\" is true"
            },
            new ReviewHint
            {
                RuleId = "OPTIONS_SECTION_NOT_VISIBLE_IN_CHANGED_CONFIG",
                Category = "Configuration/DI",
                FilePath = "Api/DI/AddDependencies.cs",
                StartLine = 100,
                Message = "Options are bound from section 'CacheOptions'.",
                Evidence = "services.Configure<CacheOptions>(configuration.GetSection(\"CacheOptions\"));"
            },
            new ReviewHint
            {
                RuleId = "GROUP_BY_FIRST_WITHOUT_ORDER",
                Category = "DataIntegrity",
                FilePath = "Infrastructure/Repositories/RegionCacheRepository.cs",
                StartLine = 112,
                Message = "GroupBy is followed by First() without a visible deterministic ordering.",
                Evidence = ".GroupBy(r => r.RegionCode) | g.First()"
            },
            new ReviewHint
            {
                RuleId = "NON_NULLABLE_CONTRACT_RETURNS_NULL",
                Category = "Contract",
                FilePath = "Infrastructure/Repositories/RegionCacheRepository.cs",
                StartLine = 87,
                Message = "A visible non-nullable method signature is close to a return null path.",
                Evidence = "public string GetRegionName(string regionCode) ... return null;"
            },
            new ReviewHint
            {
                RuleId = "JWT_UTC_COMPARED_WITH_LOCAL_TIME",
                Category = "Auth/TokenTime",
                FilePath = "Infrastructure/Auth/BaseServiceTokenService.cs",
                StartLine = 42,
                Message = "JWT token validity/expiration time appears to be compared with local server time.",
                Evidence = "_tokenObject.ValidTo < DateTime.Now.AddMinutes(1)"
            }
        };

        var findings = ReviewRunExecutor.BuildConfirmedDeterministicFindings(hints, []);

        Assert.Equal(5, findings.Count);
        Assert.All(findings, finding => Assert.Equal(FindingSeverity.Medium, finding.Severity));
        Assert.Contains(findings, finding =>
            finding.File == "Infrastructure/Db/GetOrderList.sql" &&
            finding.Category == FindingCategory.Logic &&
            finding.Title.Contains("SQL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, finding =>
            finding.File == "Api/DI/AddDependencies.cs" &&
            finding.Category == FindingCategory.Reliability &&
            finding.Title.Contains("Options", StringComparison.Ordinal));
        Assert.Contains(findings, finding =>
            finding.File == "Infrastructure/Repositories/RegionCacheRepository.cs" &&
            finding.Title.Contains("GroupBy", StringComparison.Ordinal));
        Assert.Contains(findings, finding =>
            finding.File == "Infrastructure/Repositories/RegionCacheRepository.cs" &&
            finding.Category == FindingCategory.Bug &&
            finding.Title.Contains("null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, finding =>
            finding.File == "Infrastructure/Auth/BaseServiceTokenService.cs" &&
            finding.Category == FindingCategory.Reliability &&
            finding.Title.Contains("JWT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildConfirmedDeterministicFindings_AddsFinding_ForTaskFactoryStartNewAsyncIoTokenFlow()
    {
        var hints = new[]
        {
            new ReviewHint
            {
                RuleId = "TASK_FACTORY_STARTNEW_ASYNC_IO_TOKEN_FLOW",
                Category = "Concurrency/AsyncIO",
                FilePath = "Auth/Helpers/AsyncLazy.cs",
                StartLine = 12,
                Message = "Task.Factory.StartNew/Unwrap is used to invoke an async factory while the diff also shows token/HTTP refresh flow.",
                Evidence = "base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap()) | RequestToken(CancellationToken.None) | await httpClient.PostAsync(\"/connect/token\", content, cancellationToken)"
            }
        };

        var finding = Assert.Single(ReviewRunExecutor.BuildConfirmedDeterministicFindings(hints, []));

        Assert.Equal("Auth/Helpers/AsyncLazy.cs", finding.File);
        Assert.Equal(FindingCategory.Reliability, finding.Category);
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("StartNew", finding.Title, StringComparison.Ordinal);
        Assert.Contains("CPU-bound", finding.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base(taskFactory)", finding.Suggestion, StringComparison.Ordinal);
    }
}
