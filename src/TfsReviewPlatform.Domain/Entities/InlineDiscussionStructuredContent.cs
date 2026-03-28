namespace TfsReviewPlatform.Domain.Entities;

public sealed class InlineDiscussionStructuredContent
{
    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Problems { get; init; } = [];

    public string Risk { get; init; } = string.Empty;

    public IReadOnlyList<string> Recommendations { get; init; } = [];

    public bool? ShouldPublishToTfs { get; init; }

    public string PublishToTfsReason { get; init; } = string.Empty;

    public string ExampleCodeLanguage { get; init; } = string.Empty;

    public string ExampleCode { get; init; } = string.Empty;

    public int AddedFindingsCount { get; init; }

    public IReadOnlyList<string> AddedFindings { get; init; } = [];

    public int AddedOpportunitiesCount { get; init; }

    public IReadOnlyList<string> AddedOpportunities { get; init; } = [];

    public IReadOnlyList<InlineDiscussionOpportunityItem> AddedOpportunityItems { get; init; } = [];
}
