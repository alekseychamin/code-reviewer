namespace TfsReviewPlatform.Domain.Entities;

public sealed class ExternalReviewArtifact
{
    public static ExternalReviewArtifact Empty { get; } = new();

    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public bool TimedOut { get; init; }

    public string EngineName { get; init; } = string.Empty;

    public string Status { get; init; } = "not_attempted";

    public string Message { get; init; } = string.Empty;

    public int ElapsedMilliseconds { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public IReadOnlyList<ExternalReviewCommandArtifact> Commands { get; init; } = [];
}

public sealed class ExternalReviewCommandArtifact
{
    public string Command { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public int ElapsedMilliseconds { get; init; }

    public string Artifact { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
