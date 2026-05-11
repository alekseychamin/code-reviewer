using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.ExternalReview;

public sealed class PrAgentExternalReviewEngine(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalReviewOptions> options,
    ILogger<PrAgentExternalReviewEngine> logger) : IExternalReviewEngine
{
    public async Task<ExternalReviewArtifact> RunAsync(
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return ExternalReviewArtifact.Empty;
        }

        if (run.Target.Kind != Domain.Enums.ReviewTargetKind.PullRequest ||
            string.IsNullOrWhiteSpace(run.Target.PullRequestUrl))
        {
            return new ExternalReviewArtifact
            {
                Enabled = true,
                Attempted = false,
                EngineName = opts.EngineName,
                Status = "skipped",
                Message = "External review is available only for pull-request runs."
            };
        }

        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            return new ExternalReviewArtifact
            {
                Enabled = true,
                Attempted = false,
                EngineName = opts.EngineName,
                Status = "skipped",
                Message = "ExternalReview:BaseUrl is empty."
            };
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, opts.TimeoutSeconds)));

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.PrAgent);
            var request = new PrAgentRunRequest(
                run.Target.PullRequestUrl!,
                NormalizeCommands(opts.Commands),
                opts.ResponseLanguage);
            var response = await client.PostAsJsonAsync(
                $"{opts.BaseUrl.TrimEnd('/')}/api/run",
                request,
                timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<PrAgentRunResponse>(cancellationToken: timeoutCts.Token)
                          ?? throw new InvalidOperationException("PR-Agent returned an empty response.");

            return new ExternalReviewArtifact
            {
                Enabled = true,
                Attempted = true,
                Succeeded = payload.Succeeded,
                EngineName = opts.EngineName,
                Status = payload.Succeeded ? "ready" : "partial",
                Message = payload.Succeeded ? "PR-Agent sidecar completed." : "PR-Agent sidecar completed with command errors.",
                ElapsedMilliseconds = payload.ElapsedMs,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Commands = payload.Commands.Select(command => new ExternalReviewCommandArtifact
                {
                    Command = command.Command,
                    Succeeded = command.Succeeded,
                    ElapsedMilliseconds = command.ElapsedMs,
                    Artifact = command.Artifact ?? string.Empty,
                    ErrorMessage = command.ErrorMessage ?? string.Empty
                }).ToArray()
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "PR-Agent sidecar timed out for run {RunId} after {TimeoutSeconds}s",
                run.Id,
                opts.TimeoutSeconds);
            return new ExternalReviewArtifact
            {
                Enabled = true,
                Attempted = true,
                TimedOut = true,
                EngineName = opts.EngineName,
                Status = "timed_out",
                Message = $"PR-Agent sidecar timed out after {opts.TimeoutSeconds}s.",
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PR-Agent sidecar failed for run {RunId}", run.Id);
            return new ExternalReviewArtifact
            {
                Enabled = true,
                Attempted = true,
                EngineName = opts.EngineName,
                Status = "failed",
                Message = exception.Message,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private static IReadOnlyList<string> NormalizeCommands(IReadOnlyList<string> commands)
    {
        var normalized = commands
            .Select(command => command.Trim().TrimStart('/').ToLowerInvariant())
            .Where(command => command is "describe" or "review" or "improve")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0
            ? ["describe", "review"]
            : normalized;
    }

    private sealed record PrAgentRunRequest(
        [property: JsonPropertyName("pr_url")] string PrUrl,
        [property: JsonPropertyName("commands")] IReadOnlyList<string> Commands,
        [property: JsonPropertyName("response_language")] string ResponseLanguage);

    private sealed class PrAgentRunResponse
    {
        public bool Succeeded { get; init; }

        [JsonPropertyName("elapsed_ms")]
        public int ElapsedMs { get; init; }

        public IReadOnlyList<PrAgentCommandResponse> Commands { get; init; } = [];
    }

    private sealed class PrAgentCommandResponse
    {
        public string Command { get; init; } = string.Empty;

        public bool Succeeded { get; init; }

        [JsonPropertyName("elapsed_ms")]
        public int ElapsedMs { get; init; }

        public string? Artifact { get; init; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; init; }
    }
}
