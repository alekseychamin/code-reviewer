using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface ILlmCompletionService
{
    Task<string> CompleteAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken);
}
