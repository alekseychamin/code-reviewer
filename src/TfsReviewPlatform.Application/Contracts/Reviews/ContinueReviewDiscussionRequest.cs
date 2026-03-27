using System.ComponentModel.DataAnnotations;

namespace TfsReviewPlatform.Application.Contracts.Reviews;

public sealed class ContinueReviewDiscussionRequest
{
    [Required]
    [MaxLength(4000)]
    public string Message { get; init; } = string.Empty;
}
