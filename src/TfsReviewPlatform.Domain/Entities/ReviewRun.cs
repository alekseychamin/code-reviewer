using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Domain.Entities;

public sealed class ReviewRun
{
    private readonly object _gate = new();

    public ReviewRun(
        Guid id,
        ReviewTargetDescriptor target,
        string? providerProfileId)
    {
        Id = id;
        Target = target;
        ProviderProfileId = providerProfileId;
        Status = ReviewRunStatus.Pending;
        CurrentMessage = "Queued";
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        ServiceName = target.RepositoryName ?? string.Empty;
    }

    public Guid Id { get; }

    public ReviewTargetDescriptor Target { get; }

    public string? ProviderProfileId { get; }

    public string ServiceName { get; private set; }

    public string? PullRequestTitle { get; private set; }

    public string? AuthorName { get; private set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(PullRequestTitle) ? Target.Title : PullRequestTitle;

    public ReviewRunStatus Status { get; private set; }

    public ReviewPipelineStage? CurrentStage { get; private set; }

    public int ProgressPercent { get; private set; }

    public string CurrentMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ReviewFinding> Findings { get; private set; } = [];

    public ReviewArtifacts Artifacts { get; private set; } = new();

    public bool PublishSucceeded { get; private set; }

    public void UpdateMetadata(string? serviceName, string? authorName, string? pullRequestTitle)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                ServiceName = serviceName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(authorName))
            {
                AuthorName = authorName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(pullRequestTitle))
            {
                PullRequestTitle = pullRequestTitle.Trim();
            }

            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            Status = ReviewRunStatus.Running;
            UpdatedAt = DateTimeOffset.UtcNow;
            CurrentMessage = "Review started";
        }
    }

    public void Advance(ReviewPipelineStage stage, int progressPercent, string message)
    {
        lock (_gate)
        {
            Status = ReviewRunStatus.Running;
            CurrentStage = stage;
            ProgressPercent = Math.Clamp(progressPercent, 0, 99);
            CurrentMessage = message;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void UpdateArtifacts(ReviewArtifacts artifacts)
    {
        lock (_gate)
        {
            Artifacts = artifacts;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void UpdateFindings(IReadOnlyList<ReviewFinding> findings)
    {
        lock (_gate)
        {
            Findings = findings;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Complete(
        ReviewArtifacts artifacts,
        IReadOnlyList<ReviewFinding> findings,
        bool publishSucceeded)
    {
        lock (_gate)
        {
            Status = ReviewRunStatus.Completed;
            CurrentStage = ReviewPipelineStage.Publish;
            ProgressPercent = 100;
            CurrentMessage = "Review completed";
            UpdatedAt = DateTimeOffset.UtcNow;
            Findings = findings;
            Artifacts = artifacts;
            PublishSucceeded = publishSucceeded;
            ErrorMessage = null;
        }
    }

    public void MarkPublishSucceeded()
    {
        lock (_gate)
        {
            PublishSucceeded = true;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public static ReviewRun Restore(
        Guid id,
        ReviewTargetDescriptor target,
        string? providerProfileId,
        ReviewRunStatus status,
        ReviewPipelineStage? currentStage,
        int progressPercent,
        string currentMessage,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IReadOnlyList<ReviewFinding> findings,
        ReviewArtifacts artifacts,
        bool publishSucceeded,
        string serviceName,
        string? authorName,
        string? pullRequestTitle)
    {
        return new ReviewRun(id, target, providerProfileId)
        {
            Status = status,
            CurrentStage = currentStage,
            ProgressPercent = progressPercent,
            CurrentMessage = currentMessage,
            ErrorMessage = errorMessage,
            Findings = findings,
            Artifacts = artifacts,
            PublishSucceeded = publishSucceeded,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ServiceName = serviceName,
            AuthorName = authorName,
            PullRequestTitle = pullRequestTitle
        };
    }

    public void Fail(string errorMessage)
    {
        lock (_gate)
        {
            Status = ReviewRunStatus.Failed;
            ErrorMessage = errorMessage;
            CurrentMessage = errorMessage;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
