using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Domain.Entities;

public sealed class ReviewRun
{
    private readonly object _gate = new();

    public ReviewRun(
        Guid id,
        ReviewTargetDescriptor target,
        string? providerProfileId,
        bool localOnlyMode)
    {
        Id = id;
        Target = target;
        ProviderProfileId = providerProfileId;
        LocalOnlyMode = localOnlyMode;
        Status = ReviewRunStatus.Pending;
        CurrentMessage = "Queued";
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; }

    public ReviewTargetDescriptor Target { get; }

    public string? ProviderProfileId { get; }

    public bool LocalOnlyMode { get; }

    public ReviewRunStatus Status { get; private set; }

    public ReviewPipelineStage? CurrentStage { get; private set; }

    public int ProgressPercent { get; private set; }

    public string CurrentMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ReviewFinding> Findings { get; private set; } = [];

    public ReviewArtifacts Artifacts { get; private set; } = new();

    public bool PublishSucceeded { get; private set; }

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
