using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Domain.Entities;

public sealed record ReviewFinding(
    string File,
    string LineHint,
    FindingCategory Category,
    FindingSeverity Severity,
    string Title,
    string Description,
    string ExistingCode,
    string Suggestion,
    int StartLine = 0,
    int EndLine = 0,
    Guid Id = default);
