namespace TfsReviewPlatform.Application.Models;

public sealed record DeepSeekTuiReviewProgress(
    string Message,
    int ProgressPercent,
    string EventType = "");
