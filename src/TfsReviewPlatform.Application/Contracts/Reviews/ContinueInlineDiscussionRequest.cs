using System.ComponentModel.DataAnnotations;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ContinueInlineDiscussionRequest
{
    [Required]
    [MaxLength(4000)]
    public string Message { get; init; } = string.Empty;
}
