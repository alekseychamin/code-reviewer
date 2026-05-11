using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Domain.ValueObjects;

namespace TfsReviewPlatform.Infrastructure.Persistence;

public sealed class PostgresReviewRunRepository(string connectionString) : IReviewRunRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new FlexibleReviewFindingSourceConverter(),
            new JsonStringEnumConverter()
        }
    };

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public Task AddAsync(ReviewRun run, CancellationToken cancellationToken)
        => UpsertAsync(run, cancellationToken);

    public async Task<ReviewRun?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select snapshot
            from review_runs
            where id = @id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", id);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string json ? DeserializeRun(json) : null;
    }

    public async Task<IReadOnlyList<ReviewRun>> ListForTargetAsync(
        ReviewTargetDescriptor target,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select snapshot
            from review_runs
            where target_key = @targetKey
            order by created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("targetKey", BuildTargetKey(target));
        command.Parameters.AddWithValue("limit", limit);

        var results = new List<ReviewRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                results.Add(DeserializeRun(reader.GetString(0)));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<ReviewRun>> SearchByServiceAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select snapshot
            from review_runs
            where service_name ilike @pattern escape '\'
               or repository_name ilike @pattern escape '\'
               or title ilike @pattern escape '\'
               or pull_request_url ilike @pattern escape '\'
               or source_branch ilike @pattern escape '\'
               or target_branch ilike @pattern escape '\'
            order by created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("pattern", BuildLikePattern(normalizedQuery));
        command.Parameters.AddWithValue("limit", limit);

        var results = new List<ReviewRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                results.Add(DeserializeRun(reader.GetString(0)));
            }
        }

        return results;
    }

    public async Task DeleteForTargetAsync(
        ReviewTargetDescriptor target,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            delete from review_runs
            where target_key = @targetKey;
            """;
        command.Parameters.AddWithValue("targetKey", BuildTargetKey(target));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            delete from review_runs
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReviewRun?> FindLatestCompletedForTargetAsync(
        ReviewTargetDescriptor target,
        DateTimeOffset createdBefore,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select snapshot
            from review_runs
            where target_key = @targetKey
              and status = @status
              and created_at < @createdBefore
            order by created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("targetKey", BuildTargetKey(target));
        command.Parameters.AddWithValue("status", ReviewRunStatus.Completed.ToString());
        command.Parameters.AddWithValue("createdBefore", createdBefore);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string json ? DeserializeRun(json) : null;
    }

    public Task UpdateAsync(ReviewRun run, CancellationToken cancellationToken)
        => UpsertAsync(run, cancellationToken);

    private async Task UpsertAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var snapshot = SerializeRun(run);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into review_runs (
                id,
                target_key,
                target_kind,
                status,
                title,
                provider_profile_id,
                service_name,
                author_name,
                repository_name,
                pull_request_url,
                source_branch,
                target_branch,
                created_at,
                updated_at,
                snapshot
            )
            values (
                @id,
                @targetKey,
                @targetKind,
                @status,
                @title,
                @providerProfileId,
                @serviceName,
                @authorName,
                @repositoryName,
                @pullRequestUrl,
                @sourceBranch,
                @targetBranch,
                @createdAt,
                @updatedAt,
                @snapshot
            )
            on conflict (id) do update
            set
                target_key = excluded.target_key,
                target_kind = excluded.target_kind,
                status = excluded.status,
                title = excluded.title,
                provider_profile_id = excluded.provider_profile_id,
                service_name = excluded.service_name,
                author_name = excluded.author_name,
                repository_name = excluded.repository_name,
                pull_request_url = excluded.pull_request_url,
                source_branch = excluded.source_branch,
                target_branch = excluded.target_branch,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                snapshot = excluded.snapshot;
            """;

        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("targetKey", BuildTargetKey(run.Target));
        command.Parameters.AddWithValue("targetKind", run.Target.Kind.ToString());
        command.Parameters.AddWithValue("status", run.Status.ToString());
        command.Parameters.AddWithValue("title", run.DisplayTitle);
        command.Parameters.AddWithValue("providerProfileId", (object?)run.ProviderProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("serviceName", string.IsNullOrWhiteSpace(run.ServiceName) ? DBNull.Value : run.ServiceName);
        command.Parameters.AddWithValue("authorName", string.IsNullOrWhiteSpace(run.AuthorName) ? DBNull.Value : run.AuthorName);
        command.Parameters.AddWithValue("repositoryName", (object?)run.Target.RepositoryName ?? DBNull.Value);
        command.Parameters.AddWithValue("pullRequestUrl", (object?)run.Target.PullRequestUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceBranch", (object?)run.Target.SourceBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("targetBranch", (object?)run.Target.TargetBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("createdAt", run.CreatedAt);
        command.Parameters.AddWithValue("updatedAt", run.UpdatedAt);
        command.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Jsonb) { Value = snapshot });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                create table if not exists review_runs (
                    id uuid primary key,
                    target_key text not null,
                    target_kind text not null,
                    status text not null,
                    title text not null,
                    provider_profile_id text null,
                    service_name text null,
                    author_name text null,
                    repository_name text null,
                    pull_request_url text null,
                    source_branch text null,
                    target_branch text null,
                    created_at timestamptz not null,
                    updated_at timestamptz not null,
                    snapshot jsonb not null
                );

                create index if not exists ix_review_runs_target_status_created
                    on review_runs (target_key, status, created_at desc);

                create index if not exists ix_review_runs_service_created
                    on review_runs (service_name, created_at desc);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static string SerializeRun(ReviewRun run)
    {
        var snapshot = new ReviewRunSnapshot
        {
            Id = run.Id,
            Target = run.Target,
            ProviderProfileId = run.ProviderProfileId,
            ServiceName = run.ServiceName,
            PullRequestTitle = run.PullRequestTitle,
            AuthorName = run.AuthorName,
            Status = run.Status,
            CurrentStage = run.CurrentStage,
            ProgressPercent = run.ProgressPercent,
            CurrentMessage = run.CurrentMessage,
            ErrorMessage = run.ErrorMessage,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            Findings = run.Findings,
            Artifacts = run.Artifacts,
            PublishSucceeded = run.PublishSucceeded
        };

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static ReviewRun DeserializeRun(string json)
    {
        var snapshot = JsonSerializer.Deserialize<ReviewRunSnapshot>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Could not deserialize review run snapshot from PostgreSQL.");

        return ReviewRun.Restore(
            snapshot.Id,
            snapshot.Target,
            snapshot.ProviderProfileId,
            snapshot.Status,
            snapshot.CurrentStage,
            snapshot.ProgressPercent,
            snapshot.CurrentMessage,
            snapshot.ErrorMessage,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            snapshot.Findings,
            snapshot.Artifacts,
            snapshot.PublishSucceeded,
            snapshot.ServiceName,
            snapshot.AuthorName,
            snapshot.PullRequestTitle);
    }

    private static string BuildTargetKey(ReviewTargetDescriptor target)
    {
        return target.Kind switch
        {
            ReviewTargetKind.PullRequest => $"pr|{NormalizeTargetPart(target.PullRequestUrl)}",
            ReviewTargetKind.BranchComparison =>
                $"branches|{NormalizeTargetPart(target.RepositoryPath)}|{NormalizeTargetPart(target.SourceBranch)}|{NormalizeTargetPart(target.TargetBranch)}",
            _ => $"{target.Kind}|{target.Title}"
        };
    }

    private static string NormalizeTargetPart(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string BuildLikePattern(string value)
    {
        return $"%{EscapeLike(value)}%";
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    private sealed class ReviewRunSnapshot
    {
        public Guid Id { get; init; }

        public ReviewTargetDescriptor Target { get; init; } = new(ReviewTargetKind.PullRequest, string.Empty, null, null, null, null, null);

        public string? ProviderProfileId { get; init; }

        public string ServiceName { get; init; } = string.Empty;

        public string? PullRequestTitle { get; init; }

        public string? AuthorName { get; init; }

        public ReviewRunStatus Status { get; init; }

        public ReviewPipelineStage? CurrentStage { get; init; }

        public int ProgressPercent { get; init; }

        public string CurrentMessage { get; init; } = string.Empty;

        public string? ErrorMessage { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

        public ReviewArtifacts Artifacts { get; init; } = new();

        public bool PublishSucceeded { get; init; }
    }
}
