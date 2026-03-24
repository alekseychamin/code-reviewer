namespace TfsReviewPlatform.Application.Models;

public sealed class LlmChatRequest
{
    public required string SystemPrompt { get; init; }

    public required string UserPrompt { get; init; }

    public string? Model { get; init; }

    public double Temperature { get; init; } = 0;

    public bool ExpectJson { get; init; }
}
