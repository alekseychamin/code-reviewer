namespace TfsReviewPlatform.Application.Abstractions;

public interface IBranchRepositoryLookupService
{
    Task<IReadOnlyList<string>> GetRepositorySuggestionsAsync(
        string repositoriesRoot,
        string query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetBranchSuggestionsAsync(
        string repositoryPath,
        string query,
        CancellationToken cancellationToken);
}
