namespace TfsReviewPlatform.Application.Abstractions;

public interface IRepositoryFileContentService
{
    Task<string?> TryGetFileContentAsync(
        string repositoryPath,
        string revision,
        string filePath,
        CancellationToken cancellationToken);
}
