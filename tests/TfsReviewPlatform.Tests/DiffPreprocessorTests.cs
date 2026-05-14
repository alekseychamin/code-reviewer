using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Services;

namespace TfsReviewPlatform.Tests;

public sealed class DiffPreprocessorTests
{
    [Fact]
    public void Process_FiltersIgnoredExtensions_AndBuildsChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 80
        }));

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            +++ b/src/App/Service.cs
            @@ -1,1 +1,3 @@
            +public class Service {}
            diff --git a/src/App/logo.png b/src/App/logo.png
            +++ b/src/App/logo.png
            @@ -0,0 +1 @@
            +binary
            diff --git a/tests/App/ServiceTests.cs b/tests/App/ServiceTests.cs
            +++ b/tests/App/ServiceTests.cs
            @@ -1,1 +1,3 @@
            +public class ServiceTests {}
            """;

        var result = sut.Process(diff);

        Assert.Contains("src/App/Service.cs", result.ChangedFiles);
        Assert.Contains("tests/App/ServiceTests.cs", result.ChangedFiles);
        Assert.DoesNotContain("src/App/logo.png", result.ChangedFiles);
        Assert.NotEmpty(result.Chunks);
    }

    [Fact]
    public void Process_TracksChangedFileDetails_ForAddedDeletedAndRenamedFiles()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 1000
        }));

        var diff = """
            diff --git a/src/App/NewService.cs b/src/App/NewService.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/App/NewService.cs
            @@ -0,0 +1,2 @@
            +public class NewService {}
            diff --git a/src/App/OldService.cs b/src/App/OldService.cs
            deleted file mode 100644
            --- a/src/App/OldService.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -public class OldService {}
            diff --git a/src/App/BeforeRename.cs b/src/App/AfterRename.cs
            similarity index 94%
            rename from src/App/BeforeRename.cs
            rename to src/App/AfterRename.cs
            --- a/src/App/BeforeRename.cs
            +++ b/src/App/AfterRename.cs
            @@ -1,1 +1,1 @@
            -public class BeforeRename {}
            +public class AfterRename {}
            """;

        var result = sut.Process(diff);

        var added = Assert.Single(result.ChangedFileDetails, file => file.FilePath == "src/App/NewService.cs");
        Assert.Equal(DiffFileChangeKind.Added, added.ChangeType);
        Assert.Null(added.OldPath);
        Assert.Equal("src/App/NewService.cs", added.NewPath);

        var deleted = Assert.Single(result.ChangedFileDetails, file => file.FilePath == "src/App/OldService.cs");
        Assert.Equal(DiffFileChangeKind.Deleted, deleted.ChangeType);
        Assert.Equal("src/App/OldService.cs", deleted.OldPath);
        Assert.Null(deleted.NewPath);

        var renamed = Assert.Single(result.ChangedFileDetails, file => file.FilePath == "src/App/AfterRename.cs");
        Assert.Equal(DiffFileChangeKind.Renamed, renamed.ChangeType);
        Assert.Equal("src/App/BeforeRename.cs", renamed.OldPath);
        Assert.Equal("src/App/AfterRename.cs", renamed.NewPath);
    }

    [Fact]
    public void Process_SplitsOversizedSingleFileDiffIntoMultipleChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 120
        }));

        var longPayload = new string('x', 420);
        var alphaLines = string.Join('\n', Enumerable.Range(1, 5).Select(index => $"+line {index:00} {longPayload}"));
        var betaLines = string.Join('\n', Enumerable.Range(6, 5).Select(index => $"+line {index:00} {longPayload}"));
        var diff = $"""
            diff --git a/src/App/HugeFile.cs b/src/App/HugeFile.cs
            +++ b/src/App/HugeFile.cs
            @@ -1,1 +1,6 @@
            {alphaLines}
            @@ -10,1 +15,6 @@
            {betaLines}
            """;

        var result = sut.Process(diff);

        Assert.True(result.Chunks.Count >= 2);
        Assert.All(result.Chunks, chunk => Assert.Contains("## File: 'src/App/HugeFile.cs'", chunk, StringComparison.Ordinal));
        Assert.All(result.Chunks, chunk => Assert.Contains("__new hunk__", chunk, StringComparison.Ordinal));
    }

    [Fact]
    public void Process_KeepsDeletionOnlyHunksInArtifacts_ButRemovesThemFromReviewContext()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 1000
        }));

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            --- a/src/App/Service.cs
            +++ b/src/App/Service.cs
            @@ -1,3 +1,0 @@
            -old line 1
            -old line 2
            -old line 3
            @@ -10,2 +7,3 @@
             context
            +new line 1
            +new line 2
            """;

        var result = sut.Process(diff);

        Assert.Contains("-old line 1", result.FilteredDiffText);
        Assert.DoesNotContain("-old line 1", result.ReviewContextDiffText);
        Assert.Contains("+new line 1", result.ReviewContextDiffText);
        Assert.Single(result.Chunks);
    }

    [Fact]
    public void Process_ExcludesGeneratedAndWsdlFilesOnlyFromReviewContext()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 1000
        }));

        var diff = """
            diff --git a/src/App/Connected Services/LegacyApi/Reference.cs b/src/App/Connected Services/LegacyApi/Reference.cs
            --- a/src/App/Connected Services/LegacyApi/Reference.cs
            +++ b/src/App/Connected Services/LegacyApi/Reference.cs
            @@ -1,2 +1,3 @@
            +// <auto-generated>
            +public partial class ReferenceClient {}
            diff --git a/contracts/legacy.wsdl b/contracts/legacy.wsdl
            --- a/contracts/legacy.wsdl
            +++ b/contracts/legacy.wsdl
            @@ -1,1 +1,2 @@
            +<definitions />
            diff --git a/src/App/RealService.cs b/src/App/RealService.cs
            --- a/src/App/RealService.cs
            +++ b/src/App/RealService.cs
            @@ -1,1 +1,2 @@
            +public class RealService {}
            """;

        var result = sut.Process(diff);

        Assert.Contains("Connected Services/LegacyApi/Reference.cs", result.FilteredDiffText);
        Assert.Contains("contracts/legacy.wsdl", result.FilteredDiffText);
        Assert.DoesNotContain("Connected Services/LegacyApi/Reference.cs", result.ReviewContextDiffText);
        Assert.DoesNotContain("contracts/legacy.wsdl", result.ReviewContextDiffText);
        Assert.Contains("RealService.cs", result.ReviewContextDiffText);
    }

    [Fact]
    public void Process_FormatsChunksForStructuredReviewConsumption()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Service.cs b/src/App/Service.cs
            --- a/src/App/Service.cs
            +++ b/src/App/Service.cs
            @@ -10,2 +10,3 @@ public async Task Handle()
             existing line
            +new line 1
            +new line 2
            -old line 1
            """;

        var result = sut.Process(diff);

        var chunk = Assert.Single(result.Chunks);
        Assert.Contains("## File: 'src/App/Service.cs'", chunk);
        Assert.Contains("__new hunk__", chunk);
        Assert.Contains("__old hunk__", chunk);
        Assert.Contains("Context: public async Task Handle()", chunk);
        Assert.Contains("  10  existing line", chunk);
        Assert.Contains("  11 +new line 1", chunk);
        Assert.Contains("-old line 1", chunk);
    }

    [Fact]
    public void Process_KeepsLargeFileChunksLocalToNearbyHunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 220
        }));

        var longPayload = new string('y', 420);
        var alphaLines = string.Join('\n', Enumerable.Range(1, 3).Select(index => $"+alpha {index:00} {longPayload}"));
        var betaLines = string.Join('\n', Enumerable.Range(1, 3).Select(index => $"+beta {index:00} {longPayload}"));
        var diff = $"""
            diff --git a/src/App/HugeFile.cs b/src/App/HugeFile.cs
            --- a/src/App/HugeFile.cs
            +++ b/src/App/HugeFile.cs
            @@ -10,1 +10,4 @@
            {alphaLines}
            @@ -200,1 +203,4 @@
            {betaLines}
            """;

        var result = sut.Process(diff);

        Assert.True(result.Chunks.Count >= 2);
        Assert.Contains(result.Chunks, chunk => chunk.Contains("alpha 01 ", StringComparison.Ordinal));
        Assert.Contains(result.Chunks, chunk => chunk.Contains("beta 01 ", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Chunks,
            chunk => chunk.Contains("alpha 01 ", StringComparison.Ordinal) &&
                     chunk.Contains("beta 01 ", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_DoesNotMergePrimaryReviewChunksAcrossDifferentFiles()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxPrimaryReviewChunkCharacters = 10000,
            MaxChunkCharacters = 10000,
            MergePrimaryReviewChunks = false
        }));

        var diff = """
            diff --git a/src/App/First.cs b/src/App/First.cs
            --- a/src/App/First.cs
            +++ b/src/App/First.cs
            @@ -1,1 +1,2 @@
            +public class First {}
            diff --git a/src/App/Second.cs b/src/App/Second.cs
            --- a/src/App/Second.cs
            +++ b/src/App/Second.cs
            @@ -1,1 +1,2 @@
            +public class Second {}
            """;

        var result = sut.Process(diff);

        Assert.Equal(2, result.ReviewChunks.Count);
        Assert.Contains(result.ReviewChunks, chunk => chunk.Contains("## File: 'src/App/First.cs'", StringComparison.Ordinal));
        Assert.Contains(result.ReviewChunks, chunk => chunk.Contains("## File: 'src/App/Second.cs'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.ReviewChunks,
            chunk => chunk.Contains("## File: 'src/App/First.cs'", StringComparison.Ordinal) &&
                     chunk.Contains("## File: 'src/App/Second.cs'", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_MergesPrimaryReviewChunksAcrossFiles_WhenEnabled()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxPrimaryReviewChunkCharacters = 10000,
            MaxChunkCharacters = 10000,
            MergePrimaryReviewChunks = true
        }));

        var diff = """
            diff --git a/src/App/First.cs b/src/App/First.cs
            --- a/src/App/First.cs
            +++ b/src/App/First.cs
            @@ -1,1 +1,2 @@
            +public class First {}
            diff --git a/src/App/Second.cs b/src/App/Second.cs
            --- a/src/App/Second.cs
            +++ b/src/App/Second.cs
            @@ -1,1 +1,2 @@
            +public class Second {}
            """;

        var result = sut.Process(diff);

        Assert.Single(result.ReviewChunks);
        Assert.Contains("## File: 'src/App/First.cs'", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Contains("## File: 'src/App/Second.cs'", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Equal(2, result.Chunks.Count);
    }

    [Fact]
    public void Process_SplitsOversizedFileByWholeHunksAndKeepsStructuredFormat()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 220
        }));

        var longPayload = new string('z', 420);
        var alphaLines = string.Join('\n', Enumerable.Range(1, 3).Select(index => $"+alpha {index:00} {longPayload}"));
        var betaLines = string.Join('\n', Enumerable.Range(1, 3).Select(index => $"+beta {index:00} {longPayload}"));
        var diff = $"""
            diff --git a/src/App/HugeFile.cs b/src/App/HugeFile.cs
            --- a/src/App/HugeFile.cs
            +++ b/src/App/HugeFile.cs
            @@ -10,1 +10,4 @@ public void Alpha()
             old line
            {alphaLines}
            @@ -30,1 +33,4 @@ public void Beta()
             old line
            {betaLines}
            """;

        var result = sut.Process(diff);

        Assert.True(result.Chunks.Count >= 2);
        Assert.All(result.Chunks, chunk => Assert.Contains("## File: 'src/App/HugeFile.cs'", chunk, StringComparison.Ordinal));
        Assert.All(result.Chunks, chunk => Assert.Contains("__new hunk__", chunk, StringComparison.Ordinal));
        Assert.Contains(result.Chunks, chunk => chunk.Contains("Context: public void Alpha()", StringComparison.Ordinal));
        Assert.Contains(result.Chunks, chunk => chunk.Contains("Context: public void Beta()", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Chunks,
            chunk => chunk.Contains("alpha 01 1234567890", StringComparison.Ordinal) &&
                     chunk.Contains("beta 01 1234567890", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_GeneratesSqlHints_ForRemovedBusinessFiltersAndUnusedJoin()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/Infrastructure/Db/GetOrderListForExcelFile.sql b/Infrastructure/Db/GetOrderListForExcelFile.sql
            --- a/Infrastructure/Db/GetOrderListForExcelFile.sql
            +++ b/Infrastructure/Db/GetOrderListForExcelFile.sql
            @@ -38,12 +38,10 @@
                     left join "System" sys
                               on o."SystemId" = sys."SystemId"
                     left join "ReplicBranch" rb
                               on o."RegionCode" = rb."RegionIsoCode"
                     left join "OperationReason" opr
                               on o."ReasonId" = opr."ReasonId"
            -where rb."IsBasic" is true
            -  and rb."IsBcAllowed" is true
            -  and (@orderId is null or o."OrderId" = @orderId)
            +where (@orderId is null or o."OrderId" = @orderId)
               and (@msisdn is null or o."Msisdn" = @msisdn)
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint => hint.RuleId == "SQL_REMOVED_BUSINESS_FILTER");
        Assert.Contains(result.ReviewHints, hint => hint.RuleId == "SQL_JOIN_ALIAS_ONLY_USED_IN_JOIN");
        Assert.Contains("Deterministic review hints", result.ReviewChunks[0], StringComparison.Ordinal);
        Assert.Contains("duplicate result rows", result.ReviewChunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesConfigurationHint_WhenOptionsSectionIsNotVisibleInChangedConfig()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/DI/AddDependencies.cs b/src/App/DI/AddDependencies.cs
            --- a/src/App/DI/AddDependencies.cs
            +++ b/src/App/DI/AddDependencies.cs
            @@ -100,1 +100,2 @@
            +services.Configure<CacheOptions>(configuration.GetSection("CacheOptions"));
            +services.AddHostedService<RegionCacheBackgroundService>();
            diff --git a/src/App/appsettings.json b/src/App/appsettings.json
            --- a/src/App/appsettings.json
            +++ b/src/App/appsettings.json
            @@ -430,1 +430,5 @@
            +  "_Options": {
            +    "RegionCacheTtlMinutes": 1440
            +  }
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint => hint.RuleId == "OPTIONS_SECTION_NOT_VISIBLE_IN_CHANGED_CONFIG");
        Assert.Equal("src/App/DI/AddDependencies.cs", hint.FilePath);
        Assert.Contains("CacheOptions", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesTestSeedHint_WhenChangedTestUsesSeedConstant()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/tests/App/GetOrdersFileTests.cs b/tests/App/GetOrdersFileTests.cs
            --- a/tests/App/GetOrdersFileTests.cs
            +++ b/tests/App/GetOrdersFileTests.cs
            @@ -60,2 +60,3 @@
            +query.RegionCode = new List<string> { SeedReplicBranchTable.RustRegionCode };
            +order.OrderRegionName.Should().Be(SeedReplicBranchTable.RustRegionName);
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "TEST_ASSERTION_USES_SEED_MEMBER" &&
            hint.Message.Contains("SeedReplicBranchTable.RustRegionCode", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_GeneratesDataIntegrityHint_ForGroupByFirstWithoutOrder()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/RegionCacheRepository.cs b/src/App/RegionCacheRepository.cs
            --- a/src/App/RegionCacheRepository.cs
            +++ b/src/App/RegionCacheRepository.cs
            @@ -100,1 +100,8 @@
            +var newCache = regions
            +    .GroupBy(r => r.RegionCode, StringComparer.OrdinalIgnoreCase)
            +    .ToFrozenDictionary(
            +        g => g.Key,
            +        g => g.First(),
            +        StringComparer.OrdinalIgnoreCase);
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint => hint.RuleId == "GROUP_BY_FIRST_WITHOUT_ORDER");
    }

    [Fact]
    public void Process_GeneratesCdcHints_ForNewConsumersAndInterTopicForeignKey()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/DI/AddDependencies.cs b/src/App/DI/AddDependencies.cs
            --- a/src/App/DI/AddDependencies.cs
            +++ b/src/App/DI/AddDependencies.cs
            @@ -170,1 +170,4 @@
             services.AddMultipleKafka(KafkaCrm)
            +    .AddCdcConsumer<MarkersContext, Domain.Entities.MarkerSystemRelate>(kafkaTopicsConfig.CdcRdmMarkerSystems)
            +    .AddCdcConsumer<MarkersContext, Domain.Entities.MarkerSystemClientCategory>(kafkaTopicsConfig.CdcRdmMarkerSystemClientCategories)
                 .AddConsumer<string, MarkerDetailMessage, MarkerDetailConsumerHandler>(kafkaTopicsConfig.MarkersDetails);
            diff --git a/src/App/Db/Configurations/MarkerSystemClientCategoryConfiguration.cs b/src/App/Db/Configurations/MarkerSystemClientCategoryConfiguration.cs
            --- /dev/null
            +++ b/src/App/Db/Configurations/MarkerSystemClientCategoryConfiguration.cs
            @@ -0,0 +1,16 @@
            +public class MarkerSystemClientCategoryConfiguration : IEntityTypeConfiguration<MarkerSystemClientCategory>
            +{
            +    public void Configure(EntityTypeBuilder<MarkerSystemClientCategory> entity)
            +    {
            +        entity.HasKey(e => new { e.MarkerId, e.SystemId, e.ClientCategoryId });
            +        entity.HasOne(e => e.MarkerSystemRelate)
            +            .WithMany(e => e.MarkerSystemClientCategories)
            +            .HasForeignKey(e => new { e.MarkerId, e.SystemId });
            +    }
            +}
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "CDC_ENTITY_OWNERSHIP_REVIEW" &&
            hint.Message.Contains("MarkerSystemRelate", StringComparison.Ordinal));
        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "CDC_INTER_TOPIC_FK_ORDERING" &&
            hint.Message.Contains("MarkerSystemClientCategory", StringComparison.Ordinal) &&
            hint.Message.Contains("MarkerSystemRelate", StringComparison.Ordinal));
        Assert.Contains("CDC consumer was added", string.Join('\n', result.ReviewChunks), StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesRuntimeFlowHints_ForKafkaCacheAndPollingFilters()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Services/MarkerPollingService.cs b/src/App/Services/MarkerPollingService.cs
            --- a/src/App/Services/MarkerPollingService.cs
            +++ b/src/App/Services/MarkerPollingService.cs
            @@ -40,1 +40,39 @@
            +public async Task<MarkerModelResult[]> GetMarkersAsync(
            +    long handlingId,
            +    MarkerPollingRequest request,
            +    IEnumerable<Claim> claims,
            +    CancellationToken cancellationToken = default)
            +{
            +    var offset = await _cacheHandlingService.GetSessionOffsetAsync(handlingId, request.SessionId, cancellationToken);
            +    var cachedMarkers = await _cacheHandlingService.GetHandlingMarkersAsync(handlingId, cancellationToken);
            +    var handlingClientCategory = await ResolveHandlingClientCategoryAsync(handlingId, cancellationToken);
            +    var systemId = ResolveSystemId(claims.GetSystem());
            +    var markersList = cachedMarkers
            +        .Where(marker => IsAvailableForContext(
            +            marker,
            +            systemId,
            +            request.IsIdentified,
            +            request.IsTaskNeeded,
            +            handlingClientCategory))
            +        .ToList();
            +    await _cacheHandlingService.SaveSessionOffsetAsync(handlingId, request.SessionId, offset + markersList.Count, cancellationToken);
            +    return [];
            +}
            +
            +private static bool IsAvailableForContext(
            +    HandlingMarkerCacheModel marker,
            +    int systemId,
            +    bool? isIdentified,
            +    bool? isTaskNeeded,
            +    HandlingClientCategoryCacheModel? handlingClientCategory)
            +{
            +    if (isTaskNeeded.HasValue && marker.IsTaskNeeded != isTaskNeeded.Value)
            +    {
            +        return false;
            +    }
            +
            +    if (handlingClientCategory != null
            +        && marker.ClientCategoryIds.Count > 0
            +        && !marker.ClientCategoryIds.Contains(handlingClientCategory.ClientCategoryId))
            +    {
            +        return false;
            +    }
            +
            +    return true;
            +}
            diff --git a/src/App/Services/MarkerHandlingService.cs b/src/App/Services/MarkerHandlingService.cs
            --- a/src/App/Services/MarkerHandlingService.cs
            +++ b/src/App/Services/MarkerHandlingService.cs
            @@ -60,1 +60,29 @@
            +public async Task HandleMarkerAsync(SubsMarkerMessage message, CancellationToken cancellationToken = default)
            +{
            +    var details = await _markerRepo.GetMarkerDetailsAsync(message.MarkerId, _options.MarkerSystem.DefaultSystemId, cancellationToken);
            +    var cacheModel = new HandlingMarkerCacheModel
            +    {
            +        MarkerId = message.MarkerId,
            +        SystemId = details.SystemId,
            +        IsTaskNeeded = details.IsTaskNeeded,
            +        IsAvailableAuthorizedZone = details.IsAvailableAuthorizedZone,
            +        IsAvailableUnauthorizedZone = details.IsAvailableUnauthorizedZone,
            +        ClientCategoryIds = [.. details.ClientCategoryIds]
            +    };
            +
            +    await _cacheHandlingService.SaveHandlingMarkerAsync(message.HandlingId, cacheModel, cancellationToken);
            +
            +    var viewModel = new MarkerModelResult
            +    {
            +        HandlingId = message.HandlingId,
            +        MarkerId = message.MarkerId,
            +        IsTaskNeeded = details.IsTaskNeeded
            +    };
            +
            +    await _redisPubSub.PublishAsync(viewModel);
            +}
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_FAIL_OPEN_CONTEXT_FILTER");
        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_POLLING_OFFSET_CONTEXT");
        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_STREAM_POLLING_PARITY");
        Assert.Contains(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING" &&
            hint.Category == "RuntimeFlow/KafkaCachePreFilter" &&
            hint.SuggestedVerification.Contains("Do not merge", StringComparison.Ordinal));
        Assert.Contains("Polling flow applies business filters", string.Join('\n', result.ReviewChunks), StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesKafkaCacheHint_WhenVisibilityCheckIsAfterCachePublish()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Services/MarkerHandlingService.cs b/src/App/Services/MarkerHandlingService.cs
            --- a/src/App/Services/MarkerHandlingService.cs
            +++ b/src/App/Services/MarkerHandlingService.cs
            @@ -60,1 +60,35 @@
            +public async Task HandleMarkerAsync(SubsMarkerMessage message, CancellationToken cancellationToken = default)
            +{
            +    var details = await _markerRepo.GetMarkerDetailsAsync(message.MarkerId, cancellationToken);
            +    var cacheModel = new HandlingMarkerCacheModel
            +    {
            +        MarkerId = message.MarkerId,
            +        SystemId = details.SystemId,
            +        ClientCategoryIds = [.. details.ClientCategoryIds]
            +    };
            +
            +    await _cacheHandlingService.SaveHandlingMarkerAsync(message.HandlingId, cacheModel, cancellationToken);
            +    await _redisPubSub.PublishAsync(cacheModel);
            +
            +    var handlingClientCategory = await GetHandlingClientCategoryAsync(message.HandlingId, cancellationToken);
            +    if (handlingClientCategory != null
            +        && cacheModel.ClientCategoryIds.Count > 0
            +        && !cacheModel.ClientCategoryIds.Contains(handlingClientCategory.ClientCategoryId))
            +    {
            +        return;
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING");
        Assert.Contains("SaveHandlingMarkerAsync", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains("PublishAsync", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesKafkaCacheHint_ForCacheWriteWhenServiceStreamsCachedMarkers()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Services/MarkerHandlingService.cs b/src/App/Services/MarkerHandlingService.cs
            --- a/src/App/Services/MarkerHandlingService.cs
            +++ b/src/App/Services/MarkerHandlingService.cs
            @@ -60,1 +60,23 @@
            +public async Task HandleMarkerAsync(SubsMarkerMessage message, CancellationToken cancellationToken = default)
            +{
            +    var details = await _markerRepo.GetMarkerDetailsAsync(message.MarkerId, cancellationToken);
            +    var cacheModel = new HandlingMarkerCacheModel
            +    {
            +        MarkerId = message.MarkerId,
            +        SystemId = details.SystemId,
            +        IsAvailableAuthorizedZone = details.IsAvailableAuthorizedZone,
            +        ClientCategoryIds = [.. details.ClientCategoryIds]
            +    };
            +
            +    await _cacheHandlingService.SaveHandlingMarkerAsync(message.HandlingId, cacheModel, cancellationToken);
            +}
            diff --git a/src/App/Services/MarkerNotificationService.cs b/src/App/Services/MarkerNotificationService.cs
            --- a/src/App/Services/MarkerNotificationService.cs
            +++ b/src/App/Services/MarkerNotificationService.cs
            @@ -20,1 +20,7 @@
            +public async Task StreamMarkersAsync(long handlingId, CancellationToken cancellationToken)
            +{
            +    var markers = await _cacheHandlingService.GetHandlingMarkersAsync(handlingId, cancellationToken);
            +    await ServerSentEvents.WriteAsync(markers, cancellationToken);
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING");
        Assert.Contains("SaveHandlingMarkerAsync", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains("observable marker cache outputs", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_DoesNotGenerateKafkaCacheHint_WhenVisibilityCheckPrecedesCachePublish()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Services/MarkerHandlingService.cs b/src/App/Services/MarkerHandlingService.cs
            --- a/src/App/Services/MarkerHandlingService.cs
            +++ b/src/App/Services/MarkerHandlingService.cs
            @@ -60,1 +60,35 @@
            +public async Task HandleMarkerAsync(SubsMarkerMessage message, CancellationToken cancellationToken = default)
            +{
            +    var details = await _markerRepo.GetMarkerDetailsAsync(message.MarkerId, cancellationToken);
            +    var cacheModel = new HandlingMarkerCacheModel
            +    {
            +        MarkerId = message.MarkerId,
            +        SystemId = details.SystemId,
            +        ClientCategoryIds = [.. details.ClientCategoryIds]
            +    };
            +
            +    var handlingClientCategory = await GetHandlingClientCategoryAsync(message.HandlingId, cancellationToken);
            +    if (handlingClientCategory != null
            +        && cacheModel.ClientCategoryIds.Count > 0
            +        && !cacheModel.ClientCategoryIds.Contains(handlingClientCategory.ClientCategoryId))
            +    {
            +        return;
            +    }
            +
            +    await _cacheHandlingService.SaveHandlingMarkerAsync(message.HandlingId, cacheModel, cancellationToken);
            +    await _redisPubSub.PublishAsync(cacheModel);
            +}
            """;

        var result = sut.Process(diff);

        Assert.DoesNotContain(result.ReviewHints, hint =>
            hint.RuleId == "RUNTIME_KAFKA_CACHE_PUBLISH_FILTERING");
    }

    [Fact]
    public void Process_GeneratesPageSizeLimitHint_WhenValidatorHasOnlyLowerBound()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Validators/GetItemsValidator.cs b/src/App/Validators/GetItemsValidator.cs
            --- /dev/null
            +++ b/src/App/Validators/GetItemsValidator.cs
            @@ -0,0 +1,14 @@
            +public class GetItemsValidator : AbstractValidator<GetItemsRequest>
            +{
            +    public GetItemsValidator()
            +    {
            +        RuleFor(x => x.PageSize)
            +            .GreaterThan(0)
            +            .When(x => x.PageSize.HasValue);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint => hint.RuleId == "API_UNBOUNDED_PAGE_SIZE");
        Assert.Contains("PageSize", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_DoesNotGeneratePageSizeLimitHint_WhenValidatorHasUpperBound()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Validators/GetItemsValidator.cs b/src/App/Validators/GetItemsValidator.cs
            --- /dev/null
            +++ b/src/App/Validators/GetItemsValidator.cs
            @@ -0,0 +1,14 @@
            +public class GetItemsValidator : AbstractValidator<GetItemsRequest>
            +{
            +    public GetItemsValidator()
            +    {
            +        RuleFor(x => x.PageSize)
            +            .GreaterThan(0)
            +            .LessThanOrEqualTo(100)
            +            .When(x => x.PageSize.HasValue);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        Assert.DoesNotContain(result.ReviewHints, hint => hint.RuleId == "API_UNBOUNDED_PAGE_SIZE");
    }

    [Fact]
    public void Process_GeneratesTransactionHint_WhenBulkUpdateIsFollowedByInsertWithoutTransaction()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Services/PeriodService.cs b/src/App/Services/PeriodService.cs
            --- /dev/null
            +++ b/src/App/Services/PeriodService.cs
            @@ -0,0 +1,20 @@
            +public async Task ReplaceAsync(long operatorId, CancellationToken cancellationToken)
            +{
            +    await _db.Periods
            +        .Where(x => x.OperatorId == operatorId && x.ClosedAt == null)
            +        .ExecuteUpdateAsync(set => set.SetProperty(x => x.ClosedAt, DateTime.UtcNow), cancellationToken);
            +
            +    _db.Periods.Add(new Period { OperatorId = operatorId });
            +    await _db.SaveChangesAsync(cancellationToken);
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint =>
            hint.RuleId == "EF_BULK_UPDATE_THEN_INSERT_WITHOUT_TRANSACTION");
        Assert.Contains("ExecuteUpdateAsync", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesOptionsValidationAndSecretHints()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/DI/AddConfigs.cs b/src/App/DI/AddConfigs.cs
            --- /dev/null
            +++ b/src/App/DI/AddConfigs.cs
            @@ -0,0 +1,8 @@
            +public static IServiceCollection AddConfigs(this IServiceCollection services, IConfiguration configuration)
            +{
            +    return services
            +        .Configure<PaymentOptions>(configuration.GetRequiredSection(PaymentOptions.SectionName));
            +}
            diff --git a/src/App/appsettings.Development.json b/src/App/appsettings.Development.json
            --- /dev/null
            +++ b/src/App/appsettings.Development.json
            @@ -0,0 +1,8 @@
            +{
            +  "Auth": {
            +    "IssuerSigningKey": "real-looking-key"
            +  },
            +  "ConnectionStrings": {
            +    "ConnectionDb": "Host=test-db;Username=svc;Password=password"
            +  }
            +}
            diff --git a/src/App/appsettings.Local.json b/src/App/appsettings.Local.json
            --- /dev/null
            +++ b/src/App/appsettings.Local.json
            @@ -0,0 +1,5 @@
            +{
            +  "ConnectionStrings": {
            +    "ConnectionDb": "Host=localhost;Username=postgres;Password=postgres"
            +  }
            +}
            """;

        var result = sut.Process(diff);

        Assert.Contains(result.ReviewHints, hint => hint.RuleId == "OPTIONS_BOUND_WITHOUT_VALIDATION");
        var secretHint = Assert.Single(result.ReviewHints, hint => hint.RuleId == "CONFIG_SECRET_LIKE_VALUE");
        Assert.Equal("src/App/appsettings.Development.json", secretHint.FilePath);
        Assert.Contains("***", secretHint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_GeneratesJwtUtcLocalTimeHint_WhenJwtValidToIsComparedWithDateTimeNow()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Auth/BaseServiceTokenService.cs b/src/App/Auth/BaseServiceTokenService.cs
            --- /dev/null
            +++ b/src/App/Auth/BaseServiceTokenService.cs
            @@ -0,0 +1,16 @@
            +using System.IdentityModel.Tokens.Jwt;
            +
            +public sealed class BaseServiceTokenService
            +{
            +    private JwtSecurityToken? _tokenObject;
            +
            +    public bool ShouldRefresh()
            +    {
            +        return _tokenObject is null ||
            +            _tokenObject.ValidTo < DateTime.Now.AddMinutes(1);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint => hint.RuleId == "JWT_UTC_COMPARED_WITH_LOCAL_TIME");
        Assert.Equal("src/App/Auth/BaseServiceTokenService.cs", hint.FilePath);
        Assert.Contains("ValidTo", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains("DateTime.Now", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_DoesNotGenerateJwtUtcLocalTimeHint_WhenJwtValidToIsComparedWithDateTimeUtcNow()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/App/Auth/BaseServiceTokenService.cs b/src/App/Auth/BaseServiceTokenService.cs
            --- /dev/null
            +++ b/src/App/Auth/BaseServiceTokenService.cs
            @@ -0,0 +1,16 @@
            +using System.IdentityModel.Tokens.Jwt;
            +
            +public sealed class BaseServiceTokenService
            +{
            +    private JwtSecurityToken? _tokenObject;
            +
            +    public bool ShouldRefresh()
            +    {
            +        return _tokenObject is null ||
            +            _tokenObject.ValidTo < DateTime.UtcNow.AddMinutes(1);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        Assert.DoesNotContain(result.ReviewHints, hint => hint.RuleId == "JWT_UTC_COMPARED_WITH_LOCAL_TIME");
    }

    [Fact]
    public void Process_GeneratesNonNullableContractHint_ForConditionalNullReturnInTaskGeneric()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/Auth/HttpClientsServiceOnlyAuthHandler.cs b/src/Auth/HttpClientsServiceOnlyAuthHandler.cs
            --- /dev/null
            +++ b/src/Auth/HttpClientsServiceOnlyAuthHandler.cs
            @@ -0,0 +1,18 @@
            +using System.Net.Http.Headers;
            +using System.Threading;
            +using System.Threading.Tasks;
            +
            +public sealed class HttpClientsServiceOnlyAuthHandler
            +{
            +    private async Task<AuthenticationHeaderValue> AuthorizeUsingServiceUserAsync(CancellationToken cancellationToken)
            +    {
            +        var serviceToken = await GetToken(cancellationToken);
            +
            +        return serviceToken == null ? null : new AuthenticationHeaderValue("Bearer", serviceToken);
            +    }
            +
            +    private Task<string?> GetToken(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint => hint.RuleId == "NON_NULLABLE_CONTRACT_RETURNS_NULL");
        Assert.Equal("src/Auth/HttpClientsServiceOnlyAuthHandler.cs", hint.FilePath);
        Assert.Contains("Task<AuthenticationHeaderValue>", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains("return serviceToken == null ? null", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_DoesNotGenerateNonNullableContractHint_ForNullableTaskGeneric()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/Auth/HttpClientsServiceOnlyAuthHandler.cs b/src/Auth/HttpClientsServiceOnlyAuthHandler.cs
            --- /dev/null
            +++ b/src/Auth/HttpClientsServiceOnlyAuthHandler.cs
            @@ -0,0 +1,13 @@
            +using System.Net.Http.Headers;
            +using System.Threading;
            +using System.Threading.Tasks;
            +
            +public sealed class HttpClientsServiceOnlyAuthHandler
            +{
            +    private async Task<AuthenticationHeaderValue?> AuthorizeUsingServiceUserAsync(CancellationToken cancellationToken)
            +    {
            +        var serviceToken = await GetToken(cancellationToken);
            +        return serviceToken == null ? null : new AuthenticationHeaderValue("Bearer", serviceToken);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        Assert.DoesNotContain(result.ReviewHints, hint => hint.RuleId == "NON_NULLABLE_CONTRACT_RETURNS_NULL");
    }

    [Fact]
    public void Process_GeneratesTaskFactoryStartNewAsyncIoHint_WhenAsyncLazyBacksTokenHttpRefresh()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/Auth/Helpers/AsyncLazy.cs b/src/Auth/Helpers/AsyncLazy.cs
            --- /dev/null
            +++ b/src/Auth/Helpers/AsyncLazy.cs
            @@ -0,0 +1,12 @@
            +using System;
            +using System.Threading.Tasks;
            +
            +public sealed class AsyncLazy<T> : Lazy<Task<T>>
            +{
            +    public AsyncLazy(Func<Task<T>> taskFactory) :
            +        base(() => Task.Factory.StartNew(() => taskFactory()).Unwrap())
            +    { }
            +}
            diff --git a/src/Auth/BaseServiceTokenService.cs b/src/Auth/BaseServiceTokenService.cs
            --- /dev/null
            +++ b/src/Auth/BaseServiceTokenService.cs
            @@ -0,0 +1,18 @@
            +using System.Threading;
            +using System.Threading.Tasks;
            +
            +public abstract class BaseServiceTokenService
            +{
            +    private AsyncLazy<string> _tokenAsyncLazy;
            +    protected BaseServiceTokenService()
            +    {
            +        _tokenAsyncLazy = new AsyncLazy<string>(TokenValueFactory);
            +    }
            +    protected abstract Task<string> RequestToken(CancellationToken cancellationToken);
            +    private async Task<string> TokenValueFactory()
            +    {
            +        return await RequestToken(CancellationToken.None);
            +    }
            +}
            diff --git a/src/Auth/IdentityServiceTokenService.cs b/src/Auth/IdentityServiceTokenService.cs
            --- /dev/null
            +++ b/src/Auth/IdentityServiceTokenService.cs
            @@ -0,0 +1,14 @@
            +using System.Net.Http;
            +using System.Threading;
            +using System.Threading.Tasks;
            +
            +public sealed class IdentityServiceTokenService : BaseServiceTokenService
            +{
            +    private readonly HttpClient _httpClient = new();
            +    protected override async Task<string> RequestToken(CancellationToken cancellationToken)
            +    {
            +        using var response = await _httpClient.PostAsync("/connect/token", null, cancellationToken);
            +        return await response.Content.ReadAsStringAsync();
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var hint = Assert.Single(result.ReviewHints, hint =>
            hint.RuleId == "TASK_FACTORY_STARTNEW_ASYNC_IO_TOKEN_FLOW");
        Assert.Equal("src/Auth/Helpers/AsyncLazy.cs", hint.FilePath);
        Assert.Contains("Task.Factory.StartNew", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains("RequestToken(CancellationToken.None)", hint.Evidence, StringComparison.Ordinal);
        Assert.Contains(".PostAsync", hint.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_PrependsRiskDomainClassification_ToReviewChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000,
            MaxPrimaryReviewChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/Auth/BaseServiceTokenService.cs b/src/Auth/BaseServiceTokenService.cs
            --- /dev/null
            +++ b/src/Auth/BaseServiceTokenService.cs
            @@ -0,0 +1,16 @@
            +using System.IdentityModel.Tokens.Jwt;
            +
            +public sealed class BaseServiceTokenService
            +{
            +    private JwtSecurityToken? _tokenObject;
            +
            +    public bool ShouldRefresh()
            +    {
            +        return _tokenObject is null ||
            +            _tokenObject.ValidTo < DateTime.Now.AddMinutes(1);
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var domain = Assert.Single(result.RiskDomains, domain =>
            domain.Domain == ReviewRiskDomain.AuthTokenSecurity);
        Assert.Contains(domain.Factors, factor =>
            factor.Description.Contains("JWT", StringComparison.OrdinalIgnoreCase));
        var chunk = Assert.Single(result.ReviewChunks);
        Assert.StartsWith("### Risk domain classification", chunk, StringComparison.Ordinal);
        Assert.Contains("Auth/token/security", chunk, StringComparison.Ordinal);
        Assert.Contains("JWT", chunk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## File: 'src/Auth/BaseServiceTokenService.cs'", chunk, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_UsesCompactRiskDomainClassification_InReviewChunks()
    {
        var sut = new DiffPreprocessor(Microsoft.Extensions.Options.Options.Create(new ReviewPipelineOptions
        {
            MaxChunkCharacters = 4000,
            MaxPrimaryReviewChunkCharacters = 4000
        }));

        var diff = """
            diff --git a/src/Auth/AuthClient.cs b/src/Auth/AuthClient.cs
            --- /dev/null
            +++ b/src/Auth/AuthClient.cs
            @@ -0,0 +1,20 @@
            +using System.IdentityModel.Tokens.Jwt;
            +
            +public sealed class AuthClient
            +{
            +    public async Task<string> SendAsync(HttpClient client, string token, CancellationToken cancellationToken)
            +    {
            +        var jwt = new JwtSecurityToken(token);
            +        var response = await client.PostAsJsonAsync("/token", token, cancellationToken);
            +        response.EnsureSuccessStatusCode();
            +        return jwt.ValidTo < DateTime.Now.AddMinutes(1)
            +            ? string.Empty
            +            : token;
            +    }
            +}
            """;

        var result = sut.Process(diff);

        var chunk = Assert.Single(result.ReviewChunks);
        Assert.StartsWith("### Risk domain classification", chunk, StringComparison.Ordinal);
        Assert.Contains("Lens only", chunk, StringComparison.Ordinal);
        Assert.Contains("Auth/token/security", chunk, StringComparison.Ordinal);
        Assert.Contains("signals:", chunk, StringComparison.Ordinal);
        Assert.DoesNotContain("Determining factors:", chunk, StringComparison.Ordinal);
        Assert.DoesNotContain("Review mode:", chunk, StringComparison.Ordinal);
        Assert.DoesNotContain("Check:", chunk, StringComparison.Ordinal);
        Assert.True(chunk.IndexOf("## File:", StringComparison.Ordinal) < 800);
    }

}
