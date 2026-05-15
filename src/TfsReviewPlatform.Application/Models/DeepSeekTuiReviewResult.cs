using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Models;

public sealed class DeepSeekTuiReviewResult
{
    public static DeepSeekTuiReviewResult Empty { get; } = new();

    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public bool TimedOut { get; init; }

    public string EngineName { get; init; } = "DeepSeek-TUI";

    public string Status { get; init; } = "disabled";

    public string Message { get; init; } = string.Empty;

    public string WorkspacePath { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    public int ElapsedMilliseconds { get; init; }

    public string RawOutput { get; init; } = string.Empty;

    public string ErrorOutput { get; init; } = string.Empty;

    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

    public IReadOnlyList<ReviewOpportunityItem> Opportunities { get; init; } = [];
}
