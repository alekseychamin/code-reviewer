using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewSemanticIndex
{
    Task IndexPreparedChunksAsync(ReviewRun run, CancellationToken cancellationToken);

    Task IndexFindingsAsync(ReviewRun run, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> SearchPreparedChunksAsync(
        ReviewRun run,
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SemanticFindingMatch>> SearchHistoricalFindingsAsync(
        ReviewRun run,
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task DeleteTargetAsync(ReviewTargetDescriptor target, CancellationToken cancellationToken);
}

public sealed record SemanticFindingMatch(
    Guid RunId,
    DateTimeOffset CreatedAt,
    string File,
    int StartLine,
    int EndLine,
    FindingSeverity Severity,
    FindingCategory Category,
    ReviewFindingSource Source,
    string Title,
    string Description,
    string ExistingCode,
    string Suggestion);
