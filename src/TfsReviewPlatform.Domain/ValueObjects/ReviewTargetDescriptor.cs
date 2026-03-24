using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Domain.ValueObjects;

public sealed record ReviewTargetDescriptor(
    ReviewTargetKind Kind,
    string Title,
    string? PullRequestUrl,
    string? RepositoryPath,
    string? RepositoryName,
    string? SourceBranch,
    string? TargetBranch);
