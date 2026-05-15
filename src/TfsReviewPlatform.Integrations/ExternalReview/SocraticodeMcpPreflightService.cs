using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Integrations.ExternalReview;

public sealed class SocraticodeMcpPreflightService(
    IOptions<DeepSeekTuiReviewOptions> options,
    ILogger<SocraticodeMcpPreflightService> logger) : IDeepSeekTuiSocraticodePreflight
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<DeepSeekTuiSocraticodePreflightResult> EnsureReadyAsync(
        string repositoryPath,
        string runDirectory,
        CancellationToken cancellationToken,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback = null)
    {
        var opts = options.Value;
        if (!opts.SocraticodePreflightEnabled)
        {
            return await CompleteAsync(runDirectory, new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = false,
                Status = "disabled",
                Message = "SocratiCode preflight is disabled.",
                RepositoryPath = repositoryPath
            }, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return await CompleteAsync(runDirectory, new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = true,
                Attempted = false,
                Status = "skipped",
                Message = "Repository workspace is not available for SocratiCode preflight.",
                RepositoryPath = repositoryPath
            }, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(opts.SocraticodeMcpCommand))
        {
            return await CompleteAsync(runDirectory, new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = true,
                Attempted = false,
                Status = "skipped",
                Message = "DeepSeekTuiReview:SocraticodeMcpCommand is empty.",
                RepositoryPath = repositoryPath
            }, cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var statusText = string.Empty;
        var startedIndex = false;
        var updatedIndex = false;

        try
        {
            await PublishProgressAsync(progressCallback, "SocratiCode: проверяем индекс репозитория", 51, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, opts.SocraticodePreflightTimeoutSeconds)));
            await using var client = await SocraticodeMcpClient.StartAsync(opts, repositoryPath, timeoutCts.Token);

            statusText = await client.CallToolTextAsync(
                "codebase_status",
                new Dictionary<string, object?> { ["projectPath"] = repositoryPath },
                timeoutCts.Token);

            if (IsMissingIndexStatus(statusText) || HasEmptyIndexStatus(statusText))
            {
                await PublishProgressAsync(progressCallback, "SocratiCode: индекс не найден, запускаем codebase_index", 52, cancellationToken);
                statusText = await client.CallToolTextAsync(
                    "codebase_index",
                    new Dictionary<string, object?> { ["projectPath"] = repositoryPath },
                    timeoutCts.Token);
                startedIndex = true;
            }
            else if (opts.SocraticodePreflightUpdateExistingIndex)
            {
                await PublishProgressAsync(progressCallback, "SocratiCode: обновляем существующий индекс", 52, cancellationToken);
                statusText = await client.CallToolTextAsync(
                    "codebase_update",
                    new Dictionary<string, object?> { ["projectPath"] = repositoryPath },
                    timeoutCts.Token);
                updatedIndex = true;
            }

            statusText = await WaitUntilReadyAsync(
                client,
                repositoryPath,
                opts,
                progressCallback,
                cancellationToken,
                timeoutCts.Token);

            var result = new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = true,
                Attempted = true,
                Ready = true,
                StartedIndex = startedIndex,
                UpdatedIndex = updatedIndex,
                Status = "ready",
                Message = "SocratiCode index is ready.",
                RepositoryPath = repositoryPath,
                StatusText = statusText,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds
            };
            logger.LogInformation(
                "SocratiCode preflight completed for {RepositoryPath}: startedIndex={StartedIndex}, updatedIndex={UpdatedIndex}, elapsedMs={ElapsedMs}",
                repositoryPath,
                startedIndex,
                updatedIndex,
                stopwatch.ElapsedMilliseconds);
            await PublishProgressAsync(progressCallback, "SocratiCode: индекс готов", 56, cancellationToken);
            return await CompleteAsync(runDirectory, result, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var result = new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = true,
                Attempted = true,
                Ready = false,
                StartedIndex = startedIndex,
                UpdatedIndex = updatedIndex,
                TimedOut = true,
                Status = "timed_out",
                Message = $"SocratiCode preflight timed out after {opts.SocraticodePreflightTimeoutSeconds}s.",
                RepositoryPath = repositoryPath,
                StatusText = statusText,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds
            };
            logger.LogWarning(
                "SocratiCode preflight timed out for {RepositoryPath}: elapsedMs={ElapsedMs}",
                repositoryPath,
                stopwatch.ElapsedMilliseconds);
            return await CompleteAsync(runDirectory, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var result = new DeepSeekTuiSocraticodePreflightResult
            {
                Enabled = true,
                Attempted = true,
                Ready = false,
                StartedIndex = startedIndex,
                UpdatedIndex = updatedIndex,
                Status = "failed",
                Message = exception.Message,
                RepositoryPath = repositoryPath,
                StatusText = statusText,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds
            };
            logger.LogWarning(exception, "SocratiCode preflight failed for {RepositoryPath}", repositoryPath);
            return await CompleteAsync(runDirectory, result, cancellationToken);
        }
    }

    private static async Task<string> WaitUntilReadyAsync(
        SocraticodeMcpClient client,
        string repositoryPath,
        DeepSeekTuiReviewOptions opts,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback,
        CancellationToken callbackCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(opts.SocraticodePreflightPollIntervalSeconds, 1, 30));
        var lastStatus = string.Empty;

        while (true)
        {
            lastStatus = await client.CallToolTextAsync(
                "codebase_status",
                new Dictionary<string, object?> { ["projectPath"] = repositoryPath },
                timeoutCancellationToken);

            if (IsIndexReadyStatus(lastStatus))
            {
                return lastStatus;
            }

            var percent = TryExtractProgressPercent(lastStatus);
            var message = percent is null
                ? "SocratiCode: ждем завершения индексирования"
                : $"SocratiCode: индексирование {percent.Value}%";
            await PublishProgressAsync(progressCallback, message, 53, callbackCancellationToken);
            await Task.Delay(pollDelay, timeoutCancellationToken);
        }
    }

    private static async Task PublishProgressAsync(
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback,
        string message,
        int progressPercent,
        CancellationToken cancellationToken)
    {
        if (progressCallback is null)
        {
            return;
        }

        await progressCallback(
            new DeepSeekTuiReviewProgress(message, progressPercent, message),
            cancellationToken);
    }

    private static async Task<DeepSeekTuiSocraticodePreflightResult> CompleteAsync(
        string runDirectory,
        DeepSeekTuiSocraticodePreflightResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "socraticode-preflight.json"),
                JsonSerializer.Serialize(result, JsonOptions),
                Encoding.UTF8,
                cancellationToken);
        }
        catch
        {
            // Preflight artifacts are diagnostic only; the review can continue without them.
        }

        return result;
    }

    public static bool IsMissingIndexStatus(string statusText)
    {
        return statusText.Contains("No index found", StringComparison.OrdinalIgnoreCase) ||
               statusText.Contains("not indexed", StringComparison.OrdinalIgnoreCase) ||
               statusText.Contains("Run codebase_index", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsIndexReadyStatus(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText) ||
            IsMissingIndexStatus(statusText) ||
            HasEmptyIndexStatus(statusText) ||
            statusText.Contains("Full index in progress", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("Indexing is now running", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("generating embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return statusText.Contains("Status: green", StringComparison.OrdinalIgnoreCase) &&
               statusText.Contains("Indexed chunks:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasEmptyIndexStatus(string statusText)
    {
        return TryExtractIndexedChunks(statusText) == 0;
    }

    public static int? TryExtractIndexedChunks(string statusText)
    {
        const string marker = "Indexed chunks:";
        var markerIndex = statusText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var numberStart = markerIndex + marker.Length;
        while (numberStart < statusText.Length && char.IsWhiteSpace(statusText[numberStart]))
        {
            numberStart++;
        }

        var numberEnd = numberStart;
        while (numberEnd < statusText.Length && char.IsDigit(statusText[numberEnd]))
        {
            numberEnd++;
        }

        return numberEnd == numberStart
            ? null
            : int.Parse(statusText.AsSpan(numberStart, numberEnd - numberStart));
    }

    public static int? TryExtractProgressPercent(string statusText)
    {
        var marker = statusText.IndexOf("Progress:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var percentStart = statusText.IndexOf('(', marker);
        if (percentStart < 0)
        {
            return null;
        }

        var percentEnd = statusText.IndexOf('%', percentStart);
        if (percentEnd <= percentStart + 1)
        {
            return null;
        }

        return int.TryParse(statusText.AsSpan(percentStart + 1, percentEnd - percentStart - 1), out var percent)
            ? percent
            : null;
    }

    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;

        foreach (var ch in arguments)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (escaped)
        {
            current.Append('\\');
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private sealed class SocraticodeMcpClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TextWriter _stdin;
        private readonly TextReader _stdout;
        private readonly Task _stderrTask;
        private int _nextId = 1;

        private SocraticodeMcpClient(Process process, Task stderrTask)
        {
            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            _stderrTask = stderrTask;
        }

        public static async Task<SocraticodeMcpClient> StartAsync(
            DeepSeekTuiReviewOptions opts,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = opts.SocraticodeMcpCommand.Trim(),
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in SplitArguments(opts.SocraticodeMcpArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start SocratiCode MCP process.");
            }

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(cancellationToken) is not null)
                {
                    // SocratiCode writes diagnostics to stderr. Keep the pipe drained.
                }
            }, cancellationToken);

            var client = new SocraticodeMcpClient(process, stderrTask);
            await client.InitializeAsync(cancellationToken);
            return client;
        }

        public async Task<string> CallToolTextAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var response = await SendRequestAsync(
                "tools/call",
                new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments
                },
                cancellationToken);
            return ExtractToolText(response);
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await SendRequestAsync(
                "initialize",
                new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "tfs-review-platform",
                        ["version"] = "1.0"
                    }
                },
                cancellationToken);

            await SendNotificationAsync(
                "notifications/initialized",
                new Dictionary<string, object?>(),
                cancellationToken);
        }

        private async Task<JsonElement> SendRequestAsync(
            string method,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };

            await _stdin.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
            return await ReadResponseAsync(id, cancellationToken);
        }

        private async Task SendNotificationAsync(
            string method,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters
            };

            await _stdin.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }

        private async Task<JsonElement> ReadResponseAsync(int id, CancellationToken cancellationToken)
        {
            while (await _stdout.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) ||
                    responseId.ValueKind != JsonValueKind.Number ||
                    responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(error.ToString());
                }

                return root.Clone();
            }

            throw new InvalidOperationException("SocratiCode MCP process closed stdout before returning a response.");
        }

        private static string ExtractToolText(JsonElement response)
        {
            if (!response.TryGetProperty("result", out var result))
            {
                return response.ToString();
            }

            if (!result.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return result.ToString();
            }

            var builder = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(text.GetString());
                }
            }

            return builder.Length == 0 ? result.ToString() : builder.ToString().TrimEnd();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await _stderrTask;
            }
            catch
            {
            }

            _process.Dispose();
        }
    }
}
