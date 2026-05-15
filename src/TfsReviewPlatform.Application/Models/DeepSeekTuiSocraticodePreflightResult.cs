namespace TfsReviewPlatform.Application.Models;

public sealed class DeepSeekTuiSocraticodePreflightResult
{
    public static DeepSeekTuiSocraticodePreflightResult NotAttempted { get; } = new()
    {
        Status = "not_attempted",
        Message = "SocratiCode preflight was not attempted."
    };

    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Ready { get; init; }

    public bool StartedIndex { get; init; }

    public bool UpdatedIndex { get; init; }

    public bool TimedOut { get; init; }

    public string Status { get; init; } = "disabled";

    public string Message { get; init; } = string.Empty;

    public string RepositoryPath { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public int ElapsedMilliseconds { get; init; }
}
