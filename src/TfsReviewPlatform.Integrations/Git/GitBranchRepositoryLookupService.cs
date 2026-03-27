using TfsReviewPlatform.Application.Abstractions;

namespace TfsReviewPlatform.Integrations.Git;

public sealed class GitBranchRepositoryLookupService(ShellGitCommandRunner gitCommandRunner)
    : IBranchRepositoryLookupService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> GetRepositorySuggestionsAsync(
        string repositoriesRoot,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(repositoriesRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var normalizedQuery = query.Trim();
        var allRepositories = GetOrAddCached(
            $"repos|{repositoriesRoot}",
            () => Directory
                .EnumerateDirectories(repositoriesRoot)
                .Where(static path => Directory.Exists(Path.Combine(path, ".git")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        var suggestions = allRepositories
            .Where(name => normalizedQuery.Length == 0 ||
                           name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => normalizedQuery.Length > 0 && name.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(suggestions);
    }

    public async Task<IReadOnlyList<string>> GetBranchSuggestionsAsync(
        string repositoryPath,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(repositoryPath))
        {
            return [];
        }

        var allBranches = await GetOrAddCachedAsync(
            $"branches|{repositoryPath}",
            async () =>
            {
                var output = await gitCommandRunner.RunAsync(
                    repositoryPath,
                    ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
                    cancellationToken);

                return output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static branch => !string.Equals(branch, "master", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(branch => branch, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            });

        var normalizedQuery = query.Trim();
        var suggestions = allBranches
            .Where(branch => normalizedQuery.Length == 0 ||
                             branch.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(branch => normalizedQuery.Length > 0 && branch.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(branch => branch, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        return suggestions;
    }

    private IReadOnlyList<string> GetOrAddCached(string key, Func<IReadOnlyList<string>> factory)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                return existing.Values;
            }
        }

        var values = factory();

        lock (_cacheGate)
        {
            _cache[key] = new CacheEntry(values, now.Add(CacheTtl));
        }

        return values;
    }

    private async Task<IReadOnlyList<string>> GetOrAddCachedAsync(string key, Func<Task<IReadOnlyList<string>>> factory)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                return existing.Values;
            }
        }

        var values = await factory();

        lock (_cacheGate)
        {
            _cache[key] = new CacheEntry(values, now.Add(CacheTtl));
        }

        return values;
    }

    private sealed record CacheEntry(
        IReadOnlyList<string> Values,
        DateTimeOffset ExpiresAt);
}
