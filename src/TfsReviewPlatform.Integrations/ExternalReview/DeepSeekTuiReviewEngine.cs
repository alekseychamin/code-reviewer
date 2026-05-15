using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.ExternalReview;

public sealed class DeepSeekTuiReviewEngine(
    IOptions<DeepSeekTuiReviewOptions> options,
    IDeepSeekTuiSocraticodePreflight socraticodePreflight,
    ILogger<DeepSeekTuiReviewEngine> logger) : IDeepSeekTuiReviewEngine
{
    private static readonly object RepositoryWorkspaceSyncLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<DeepSeekTuiReviewResult> RunAsync(
        ReviewRun run,
        ExternalReviewInput input,
        CancellationToken cancellationToken,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback = null)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return DeepSeekTuiReviewResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(opts.ExecutablePath))
        {
            return Skipped(opts, "DeepSeekTuiReview:ExecutablePath is empty.");
        }

        if (!string.IsNullOrWhiteSpace(opts.ApiKeyEnvironmentVariable) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.ApiKeyEnvironmentVariable)))
        {
            return Skipped(opts, $"Environment variable {opts.ApiKeyEnvironmentVariable} is empty.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        PreparedDeepSeekWorkspace workspace;
        try
        {
            workspace = PrepareWorkspace(run, input, opts);
            workspace = await PrepareSocraticodePreflightAsync(
                workspace,
                input,
                cancellationToken,
                progressCallback);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DeepSeek-TUI workspace preparation failed for run {RunId}", run.Id);
            return new DeepSeekTuiReviewResult
            {
                Enabled = true,
                Attempted = true,
                Succeeded = false,
                EngineName = opts.EngineName,
                Status = "workspace_failed",
                Message = exception.Message,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds
            };
        }

        var startInfo = BuildStartInfo(opts, workspace.WorkingDirectory, workspace.Prompt);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var progressTracker = new StreamProgressTracker(opts.EngineName);

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start DeepSeek-TUI process.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, opts.TimeoutSeconds)));
            var stdoutTask = ReadStdoutAsync(
                process.StandardOutput,
                stdout,
                opts.MaxStdoutCharacters,
                progressTracker,
                progressCallback,
                cancellationToken,
                timeoutCts.Token);
            var stderrTask = ReadStderrAsync(process.StandardError, stderr, opts.MaxStderrCharacters, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
                await IgnoreCanceledReaderAsync(stdoutTask);
                await IgnoreCanceledReaderAsync(stderrTask);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
                await IgnoreCanceledReaderAsync(stdoutTask);
                await IgnoreCanceledReaderAsync(stderrTask);
                var timedOutRawOutput = stdout.ToString();
                var timedOutErrorOutput = stderr.ToString();
                WriteProcessArtifacts(workspace.RunDirectory, timedOutRawOutput, timedOutErrorOutput);
                return new DeepSeekTuiReviewResult
                {
                    Enabled = true,
                    Attempted = true,
                    Succeeded = false,
                    TimedOut = true,
                    EngineName = opts.EngineName,
                    Status = "timed_out",
                    Message = $"DeepSeek-TUI timed out after {opts.TimeoutSeconds}s.",
                    WorkspacePath = workspace.RunDirectory,
                    ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
                    RawOutput = timedOutRawOutput,
                    ErrorOutput = timedOutErrorOutput
                };
            }

            var rawOutput = stdout.ToString();
            var errorOutput = stderr.ToString();
            WriteProcessArtifacts(workspace.RunDirectory, rawOutput, errorOutput);
            var parsed = ParseReviewPayload(rawOutput);
            var succeeded = process.ExitCode == 0 && parsed is not null;
            if (!succeeded)
            {
                logger.LogWarning(
                    "DeepSeek-TUI review did not produce parseable JSON for run {RunId}: exitCode={ExitCode}, stdoutChars={StdoutChars}, stderrChars={StderrChars}, workspace={Workspace}",
                    run.Id,
                    process.ExitCode,
                    rawOutput.Length,
                    errorOutput.Length,
                    workspace.RunDirectory);
            }

            return new DeepSeekTuiReviewResult
            {
                Enabled = true,
                Attempted = true,
                Succeeded = succeeded,
                EngineName = opts.EngineName,
                Status = succeeded ? "ready" : "failed",
                Message = succeeded
                    ? "DeepSeek-TUI review completed."
                    : "DeepSeek-TUI review finished without parseable structured findings.",
                WorkspacePath = workspace.RunDirectory,
                ExitCode = process.ExitCode,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
                RawOutput = rawOutput,
                ErrorOutput = errorOutput,
                Findings = parsed?.Findings ?? [],
                Opportunities = parsed?.Opportunities ?? []
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var rawOutput = stdout.ToString();
            var errorOutput = stderr.ToString();
            WriteProcessArtifacts(workspace.RunDirectory, rawOutput, errorOutput);
            logger.LogWarning(exception, "DeepSeek-TUI review failed for run {RunId}", run.Id);
            return new DeepSeekTuiReviewResult
            {
                Enabled = true,
                Attempted = true,
                Succeeded = false,
                EngineName = opts.EngineName,
                Status = "failed",
                Message = exception.Message,
                WorkspacePath = workspace.RunDirectory,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
                RawOutput = rawOutput,
                ErrorOutput = errorOutput
            };
        }
        finally
        {
            logger.LogInformation(
                "DeepSeek-TUI review finished for run {RunId}: elapsedMs={ElapsedMs}",
                run.Id,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public static ParsedDeepSeekReview? ParseReviewPayload(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return null;
        }

        foreach (var candidate in EnumerateJsonCandidates(rawOutput).Reverse())
        {
            var parsed = TryParseCandidate(candidate);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        var assistantText = ExtractAssistantText(rawOutput);
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return null;
        }

        foreach (var candidate in EnumerateJsonCandidates(assistantText).Reverse())
        {
            var parsed = TryParseCandidate(candidate);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static DeepSeekTuiReviewResult Skipped(DeepSeekTuiReviewOptions opts, string message)
    {
        return new DeepSeekTuiReviewResult
        {
            Enabled = true,
            Attempted = false,
            Succeeded = false,
            EngineName = opts.EngineName,
            Status = "skipped",
            Message = message
        };
    }

    private static PreparedDeepSeekWorkspace PrepareWorkspace(
        ReviewRun run,
        ExternalReviewInput input,
        DeepSeekTuiReviewOptions opts)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(opts.WorkspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "tfs-review-deepseek")
            : opts.WorkspaceRoot;
        var runDirectory = Path.Combine(
            workspaceRoot,
            string.IsNullOrWhiteSpace(opts.RunDirectoryName) ? "runs" : opts.RunDirectoryName,
            run.Id.ToString("N"));

        if (Directory.Exists(runDirectory))
        {
            Directory.Delete(runDirectory, true);
        }

        Directory.CreateDirectory(runDirectory);

        var diffPath = Path.Combine(runDirectory, "diff.patch");
        File.WriteAllText(diffPath, input.DiffText ?? string.Empty, Encoding.UTF8);

        string? repositoryWorkspace = null;
        if (opts.CopyRepositoryToWorkspace &&
            !string.IsNullOrWhiteSpace(input.RepositoryPath) &&
            Directory.Exists(input.RepositoryPath))
        {
            var excludedDirectories = BuildExcludedDirectorySet(opts);
            if (opts.UsePersistentRepositoryWorkspace)
            {
                repositoryWorkspace = BuildPersistentRepositoryWorkspacePath(workspaceRoot, opts, input, run);
                lock (RepositoryWorkspaceSyncLock)
                {
                    SyncDirectory(input.RepositoryPath, repositoryWorkspace, excludedDirectories);
                }
            }
            else
            {
                repositoryWorkspace = Path.Combine(runDirectory, "repository");
                CopyDirectory(input.RepositoryPath, repositoryWorkspace, excludedDirectories);
            }
        }

        var metadata = new
        {
            run_id = run.Id,
            pull_request_url = input.PullRequestUrl ?? run.Target.PullRequestUrl,
            repository = input.RepositoryName ?? run.Target.RepositoryName,
            service_name = input.ServiceName ?? run.ServiceName,
            title = input.PullRequestTitle ?? run.PullRequestTitle ?? run.DisplayTitle,
            source_ref = input.SourceRef ?? run.Target.SourceBranch,
            target_ref = input.TargetRef ?? run.Target.TargetBranch,
            changed_files = input.ChangedFiles,
            risk_domains = input.RiskDomainsSummary,
            diff_path = diffPath,
            repository_path = repositoryWorkspace,
            repository_remote_url = input.RepositoryRemoteUrl,
            git_fetch_source_ref = input.GitFetchSourceRef,
            git_fetch_target_ref = input.GitFetchTargetRef
        };
        var metadataPath = Path.Combine(runDirectory, "review-input.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8);

        var prompt = BuildReviewPrompt(
            metadataPath,
            diffPath,
            repositoryWorkspace,
            input.ChangedFiles,
            input.RiskDomainsSummary,
            DeepSeekTuiSocraticodePreflightResult.NotAttempted);
        var promptPath = Path.Combine(runDirectory, "review-prompt.md");
        File.WriteAllText(promptPath, prompt, Encoding.UTF8);

        return new PreparedDeepSeekWorkspace(
            runDirectory,
            runDirectory,
            prompt,
            promptPath,
            metadataPath,
            diffPath,
            repositoryWorkspace,
            input.ChangedFiles,
            input.RiskDomainsSummary);
    }

    private async Task<PreparedDeepSeekWorkspace> PrepareSocraticodePreflightAsync(
        PreparedDeepSeekWorkspace workspace,
        ExternalReviewInput input,
        CancellationToken cancellationToken,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback)
    {
        var preflight = string.IsNullOrWhiteSpace(workspace.RepositoryWorkspace)
            ? DeepSeekTuiSocraticodePreflightResult.NotAttempted
            : await socraticodePreflight.EnsureReadyAsync(
                workspace.RepositoryWorkspace,
                workspace.RunDirectory,
                cancellationToken,
                progressCallback);

        var prompt = BuildReviewPrompt(
            workspace.MetadataPath,
            workspace.DiffPath,
            workspace.RepositoryWorkspace,
            workspace.ChangedFiles,
            workspace.RiskDomainsSummary,
            preflight);
        await File.WriteAllTextAsync(workspace.PromptPath, prompt, Encoding.UTF8, cancellationToken);

        var metadata = new
        {
            socraticode_preflight = new
            {
                preflight.Status,
                preflight.Ready,
                preflight.StartedIndex,
                preflight.UpdatedIndex,
                preflight.Message
            },
            repository_path = workspace.RepositoryWorkspace
        };
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RunDirectory, "socraticode-preflight-summary.json"),
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        return workspace with { Prompt = prompt };
    }

    private static ProcessStartInfo BuildStartInfo(
        DeepSeekTuiReviewOptions opts,
        string workingDirectory,
        string prompt)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = opts.ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(opts.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(opts.Model.Trim());
        }

        if (opts.AutoApproveTools)
        {
            startInfo.ArgumentList.Add("--yolo");
        }

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--auto");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add(prompt);
        startInfo.Environment["DEEPSEEK_NO_COLOR"] = "1";
        return startInfo;
    }

    private static string BuildReviewPrompt(
        string metadataPath,
        string diffPath,
        string? repositoryWorkspace,
        IReadOnlyList<string> changedFiles,
        string riskDomainsSummary,
        DeepSeekTuiSocraticodePreflightResult socraticodePreflight)
    {
        var files = changedFiles.Count == 0
            ? "(not provided)"
            : string.Join('\n', changedFiles.Select(file => $"- {file}"));
        var repositoryInstruction = string.IsNullOrWhiteSpace(repositoryWorkspace)
            ? "Full repository copy is not available; use diff.patch and review-input.json only."
            : $"Full repository copy is available at `{repositoryWorkspace}`. Treat it as an external context path; inspect source files only after the SocratiCode preflight says the project is indexed or updated.";
        var riskDomainsBlock = string.IsNullOrWhiteSpace(riskDomainsSummary)
            ? "No deterministic risk domain summary was provided. Infer active lenses from changed paths and diff content."
            : riskDomainsSummary.Trim();
        var socraticodePreflightBlock = BuildSocraticodePreflightPromptBlock(repositoryWorkspace, socraticodePreflight);

        return $$"""
            You are a senior backend code reviewer for a .NET service.

            Review the pull request using the prepared files:
            - Metadata: `{{metadataPath}}`
            - Diff: `{{diffPath}}`
            - {{repositoryInstruction}}

            Runtime layout:
            - Your current working directory is the review run directory, not the repository.
            - Start by reading `diff.patch` and `review-input.json` from the current directory.

            SocratiCode application preflight:
            {{socraticodePreflightBlock}}

            Changed files:
            {{files}}

            Deterministic risk domain summary:
            {{riskDomainsBlock}}

            Repository navigation tools:
            - SocratiCode MCP is the preferred tool for repository-wide evidence: semantic search, symbol lookup, callers/callees, impact/blast-radius, execution flow, dependency graph, and context artifacts.
            - Do not search the repository before understanding the diff. First read `diff.patch`, classify risk domains, and write concrete hypotheses.
            - DeepSeek-TUI exposes SocratiCode tools with single underscores, for example `mcp_socraticode_codebase_status`.
            - If application preflight is ready, do not call `codebase_status`, `codebase_index`, or `codebase_update`; the application already completed that work before launching you.
            - With ready preflight, use at least one targeted SocratiCode evidence tool before broad repository reads for the highest-risk hypothesis: `mcp_socraticode_codebase_search`, `mcp_socraticode_codebase_symbols`, `mcp_socraticode_codebase_symbol`, `mcp_socraticode_codebase_impact`, or `mcp_socraticode_codebase_flow` (or the same names without the `mcp_socraticode_` prefix).
            - If application preflight is not ready, you may call status/index/update yourself once, but do not spend the review budget polling indefinitely.
            - Use SocratiCode to decide which files/symbols matter, then use direct file reads only to verify exact changed code and nearby implementation details. Do not rely only on `read_file`/`grep_files` for cross-file evidence when SocratiCode is available.
            - If SocratiCode is unavailable, stale, or fails, continue with direct file inspection and note that limitation in `review_trace`.

            Required two-phase review protocol:

            Phase 1 — classify and plan:
            - Read `diff.patch` before using repository search. Select active risk lenses from the deterministic summary and from the diff itself. Do not treat a lens as a finding.
            - For each active lens, create 1-4 concrete hypotheses about changed behavior that could regress.
            - Each hypothesis must name: changed file/symbol, old-vs-new behavior to verify, and exact repository evidence needed.
            - Prefer high-signal domains when applicable: config/DI/options, SQL/EF/data integrity, cache/state/TTL, background jobs/concurrency, HTTP/integration, API contract/validation, serialization/mapping, observability/operability, build/deploy, tests/fixtures, auth/token/security.

            Phase 2 — verify:
            - Use SocratiCode and direct file reads only to confirm or reject Phase 1 hypotheses that need caller/callee/config/test evidence.
            - For cross-file evidence, prefer SocratiCode over broad manual grep/read_file loops; avoid broad wandering.
            - Mark each hypothesis as confirmed, rejected, or uncertain. Only confirmed hypotheses may become findings.
            - Do not report style preferences, missing tests, or nice-to-have improvements as findings unless they cause a concrete regression risk.

            Quality gate before returning:
            - Every finding must name the changed behavior/regression risk, cite concrete changed file/symbol evidence, and explain why it is not merely a preference.
            - Deduplicate by root cause. If several files show the same defect, keep the strongest finding and mention affected siblings in its description.
            - If evidence is uncertain, move it to opportunities or omit it.
            - Test-only maintainability concerns belong in opportunities, not findings: manual test helpers, reflection on private methods, duplicated test setup, inconsistent test style, or missing edge-case tests are not findings unless the diff shows a concrete compile failure, failing/flaky test, wrong assertion, or production behavior risk.
            - If there are no concrete issues, return an empty findings array.

            Return only one JSON object. Do not include markdown outside JSON.
            Keep review_trace concise; it is an audit summary, not private chain-of-thought.
            Schema:
            {
              "summary": "short Russian summary",
              "review_trace": {
                "active_lenses": [
                  {
                    "lens": "Config/DI/options",
                    "why_active": "short evidence from path/diff",
                    "checked_files": ["relative/path.cs"]
                  }
                ],
                "hypotheses": [
                  {
                    "lens": "Config/DI/options",
                    "hypothesis": "specific changed behavior to verify",
                    "evidence": "changed line/context checked",
                    "verdict": "confirmed|rejected|uncertain",
                    "finding_title": "title when confirmed, otherwise empty"
                  }
                ]
              },
              "findings": [
                {
                  "file": "relative/path.cs",
                  "line_hint": "line or symbol hint",
                  "start_line": 0,
                  "end_line": 0,
                  "category": "Security|Performance|Architecture|Bug|Reliability|Logic|CodeStyle",
                  "severity": "Critical|High|Medium|Low",
                  "title": "Russian title",
                  "description": "Russian explanation with exact risk",
                  "existing_code": "short code fragment or symbol name",
                  "suggestion": "Russian concrete fix"
                }
              ],
              "opportunities": [
                {
                  "file": "relative/path.cs",
                  "line_hint": "line or symbol hint",
                  "start_line": 0,
                  "title": "Russian title",
                  "description": "Russian explanation",
                  "suggestion": "Russian concrete improvement"
                }
              ]
            }
            """;
    }

    private static string BuildSocraticodePreflightPromptBlock(
        string? repositoryWorkspace,
        DeepSeekTuiSocraticodePreflightResult preflight)
    {
        if (string.IsNullOrWhiteSpace(repositoryWorkspace))
        {
            return "- Full repository copy is unavailable, so SocratiCode repository indexing was skipped.";
        }

        if (preflight.Ready)
        {
            var action = preflight.StartedIndex
                ? "created"
                : preflight.UpdatedIndex
                    ? "updated"
                    : "checked";
            return string.Join('\n',
                $"- READY: the application already {action} the SocratiCode index for `{repositoryWorkspace}` before launching you.",
                "- Start with `diff.patch`, classify risk domains, then use targeted SocratiCode search/symbol/impact/flow tools for hypotheses that need repository context.",
                "- Do not call SocratiCode status/index/update again unless a targeted SocratiCode evidence tool reports that the index is unavailable.");
        }

        var status = string.IsNullOrWhiteSpace(preflight.Status) ? "not_ready" : preflight.Status;
        var message = string.IsNullOrWhiteSpace(preflight.Message) ? "no message" : preflight.Message;
        return string.Join('\n',
            $"- NOT READY: application SocratiCode preflight status is `{status}` for `{repositoryWorkspace}`.",
            $"- Reason: {message}",
            "- You may try SocratiCode status/index/update once if repository context is essential; otherwise continue with focused direct file reads and mention the limitation in review_trace.");
    }

    private static HashSet<string> BuildExcludedDirectorySet(DeepSeekTuiReviewOptions opts)
    {
        return opts.ExcludedRepositoryDirectories
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory, HashSet<string> excludedDirectories)
    {
        var source = new DirectoryInfo(sourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(targetDirectory, file.Name), overwrite: true);
        }

        foreach (var directory in source.EnumerateDirectories())
        {
            if (excludedDirectories.Contains(directory.Name))
            {
                continue;
            }

            CopyDirectory(directory.FullName, Path.Combine(targetDirectory, directory.Name), excludedDirectories);
        }
    }

    private static string BuildPersistentRepositoryWorkspacePath(
        string workspaceRoot,
        DeepSeekTuiReviewOptions opts,
        ExternalReviewInput input,
        ReviewRun run)
    {
        var repositoryRoot = Path.Combine(
            workspaceRoot,
            string.IsNullOrWhiteSpace(opts.RepositoryDirectoryName) ? "repositories" : opts.RepositoryDirectoryName.Trim());
        var stableIdentity = input.RepositoryRemoteUrl ??
                             input.RepositoryName ??
                             run.Target.RepositoryName ??
                             input.PullRequestUrl ??
                             input.RepositoryPath ??
                             run.Id.ToString("N");
        var slug = Slugify(input.RepositoryName ?? run.Target.RepositoryName ?? Path.GetFileName(input.RepositoryPath) ?? "repository");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity.Trim())))
            .ToLowerInvariant();

        return Path.Combine(repositoryRoot, $"{slug}-{hash[..12]}");
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousSeparator = false;
                continue;
            }

            if (!previousSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            return "repository";
        }

        return slug.Length <= 64 ? slug : slug[..64].Trim('-');
    }

    private static void SyncDirectory(string sourceDirectory, string targetDirectory, HashSet<string> excludedDirectories)
    {
        var source = new DirectoryInfo(sourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        Directory.CreateDirectory(targetDirectory);
        var target = new DirectoryInfo(targetDirectory);

        var sourceFiles = source.EnumerateFiles()
            .ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var targetFile in target.EnumerateFiles())
        {
            if (!sourceFiles.ContainsKey(targetFile.Name))
            {
                targetFile.Delete();
            }
        }

        foreach (var sourceFile in sourceFiles.Values)
        {
            sourceFile.CopyTo(Path.Combine(targetDirectory, sourceFile.Name), overwrite: true);
        }

        var sourceDirectories = source.EnumerateDirectories()
            .Where(directory => !excludedDirectories.Contains(directory.Name))
            .ToDictionary(directory => directory.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var targetSubdirectory in target.EnumerateDirectories())
        {
            if (excludedDirectories.Contains(targetSubdirectory.Name) ||
                !sourceDirectories.ContainsKey(targetSubdirectory.Name))
            {
                targetSubdirectory.Delete(recursive: true);
            }
        }

        foreach (var sourceSubdirectory in sourceDirectories.Values)
        {
            SyncDirectory(
                sourceSubdirectory.FullName,
                Path.Combine(targetDirectory, sourceSubdirectory.Name),
                excludedDirectories);
        }
    }

    private static void AppendBoundedLine(StringBuilder builder, string? line, int maxCharacters)
    {
        if (line is null || maxCharacters <= 0)
        {
            return;
        }

        var value = line + Environment.NewLine;
        if (value.Length >= maxCharacters)
        {
            builder.Clear();
            builder.Append(value.AsSpan(value.Length - maxCharacters));
            return;
        }

        builder.Append(value);
        if (builder.Length > maxCharacters)
        {
            builder.Remove(0, builder.Length - maxCharacters);
        }
    }

    private static void WriteProcessArtifacts(string runDirectory, string rawOutput, string errorOutput)
    {
        try
        {
            Directory.CreateDirectory(runDirectory);
            File.WriteAllText(Path.Combine(runDirectory, "deepseek-stdout.tail.jsonl"), rawOutput, Encoding.UTF8);
            File.WriteAllText(Path.Combine(runDirectory, "deepseek-stderr.tail.log"), errorOutput, Encoding.UTF8);
        }
        catch
        {
            // Debug artifacts are best-effort; the review result should not fail because of filesystem cleanup issues.
        }
    }

    private static async Task ReadStdoutAsync(
        TextReader reader,
        StringBuilder stdout,
        int maxCharacters,
        StreamProgressTracker progressTracker,
        Func<DeepSeekTuiReviewProgress, CancellationToken, ValueTask>? progressCallback,
        CancellationToken callbackCancellationToken,
        CancellationToken readCancellationToken)
    {
        while (await reader.ReadLineAsync(readCancellationToken) is { } line)
        {
            AppendBoundedLine(stdout, line, maxCharacters);
            if (progressCallback is null ||
                progressTracker.TryBuild(line) is not { } progress)
            {
                continue;
            }

            try
            {
                await progressCallback(progress, callbackCancellationToken);
            }
            catch (OperationCanceledException) when (callbackCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Progress is best-effort; the final DeepSeek-TUI result should still be consumed.
            }
        }
    }

    private static async Task ReadStderrAsync(
        TextReader reader,
        StringBuilder stderr,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            AppendBoundedLine(stderr, line, maxCharacters);
        }
    }

    private static async Task IgnoreCanceledReaderAsync(Task readerTask)
    {
        try
        {
            await readerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string ExtractAssistantText(string rawOutput)
    {
        var builder = new StringBuilder();
        foreach (var line in rawOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                AppendTextFields(document.RootElement, builder);
            }
            catch (JsonException)
            {
                builder.AppendLine(trimmed);
            }
        }

        return builder.ToString();
    }

    private static void AppendTextFields(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        IsTextProperty(property.Name))
                    {
                        builder.Append(property.Value.GetString());
                    }
                    else
                    {
                        AppendTextFields(property.Value, builder);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendTextFields(item, builder);
                }

                break;
        }
    }

    private static bool IsTextProperty(string name)
    {
        return name.Equals("content", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("message", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("output", StringComparison.OrdinalIgnoreCase);
    }

    public static string? DescribeProgressLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var eventType = GetString(root, "type", "event", "event_type", "kind", "role") ?? string.Empty;
            var eventTypeLower = eventType.Trim().ToLowerInvariant();
            var isToolEvent = eventTypeLower.Contains("tool", StringComparison.Ordinal) ||
                              eventTypeLower.Contains("mcp", StringComparison.Ordinal) ||
                              eventTypeLower.Contains("function", StringComparison.Ordinal);
            var toolName = FindToolName(root, includeGenericName: isToolEvent);
            if (isToolEvent || toolName is not null)
            {
                if (eventTypeLower.Contains("result", StringComparison.Ordinal) ||
                    eventTypeLower.Contains("output", StringComparison.Ordinal) ||
                    eventTypeLower.Contains("done", StringComparison.Ordinal) ||
                    eventTypeLower.Contains("finish", StringComparison.Ordinal))
                {
                    return toolName is null
                        ? "получил результат инструмента"
                        : $"получил результат инструмента {toolName}";
                }

                return toolName is null
                    ? "использует инструмент для чтения контекста"
                    : $"использует инструмент {toolName}";
            }

            var text = ExtractAssistantText(rawLine);
            if (LooksLikeFinalJson(text))
            {
                return "формирует структурированный JSON ревью";
            }

            if (eventTypeLower.Contains("assistant", StringComparison.Ordinal) ||
                eventTypeLower.Contains("message", StringComparison.Ordinal) ||
                eventTypeLower.Contains("delta", StringComparison.Ordinal))
            {
                return "анализирует найденный контекст";
            }

            if (eventTypeLower.Contains("error", StringComparison.Ordinal))
            {
                return "получил диагностическое сообщение";
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool LooksLikeFinalJson(string text)
    {
        return text.Contains("\"findings\"", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("\"opportunities\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindToolName(JsonElement element, bool includeGenericName)
    {
        if (TryFindToolName(element, includeGenericName, out var toolName))
        {
            return toolName;
        }

        return ContainsString(element, "socraticode")
            ? "SocratiCode MCP"
            : null;
    }

    private static bool TryFindToolName(JsonElement element, bool includeGenericName, out string? toolName)
    {
        toolName = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindToolName(item, includeGenericName, out toolName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                IsToolNameProperty(property.Name, includeGenericName) &&
                SanitizeToolName(property.Value.GetString()) is { } value)
            {
                toolName = value;
                return true;
            }

            if (TryFindToolName(property.Value, includeGenericName, out toolName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsToolNameProperty(string propertyName, bool includeGenericName)
    {
        return propertyName.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("tool_name", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("toolName", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("function", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("command", StringComparison.OrdinalIgnoreCase) ||
               (includeGenericName && propertyName.Equals("name", StringComparison.OrdinalIgnoreCase));
    }

    private static string? SanitizeToolName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Length > 80 ||
            trimmed.Contains('\n') ||
            trimmed.Contains('\r'))
        {
            return null;
        }

        return trimmed;
    }

    private static bool ContainsString(JsonElement element, string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsString(property.Value, value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsString(item, value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static IReadOnlyList<string> EnumerateJsonCandidates(string text)
    {
        var candidates = new List<string>();
        candidates.AddRange(EnumerateFencedJsonBlocks(text));

        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                if (depth == 0)
                {
                    start = index;
                }

                depth++;
            }
            else if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    candidates.Add(text[start..(index + 1)]);
                    start = -1;
                }
            }
        }

        return candidates;
    }

    private static IEnumerable<string> EnumerateFencedJsonBlocks(string text)
    {
        const string fence = "```";
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf(fence, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var contentStart = start + fence.Length;
            if (contentStart < text.Length && text[contentStart] != '\n' && text[contentStart] != '\r')
            {
                var lineEnd = text.IndexOf('\n', contentStart);
                contentStart = lineEnd < 0 ? text.Length : lineEnd + 1;
            }

            var end = text.IndexOf(fence, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            var block = text[contentStart..end].Trim();
            if (block.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                block = block[4..].Trim();
            }

            if (block.StartsWith('{'))
            {
                yield return block;
            }

            index = end + fence.Length;
        }
    }

    private static ParsedDeepSeekReview? TryParseCandidate(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetProperty(document.RootElement, "findings", out var findingsNode) &&
                !TryGetProperty(document.RootElement, "opportunities", out _))
            {
                return null;
            }

            var findings = findingsNode.ValueKind == JsonValueKind.Array
                ? findingsNode.EnumerateArray().Select(MapFinding).Where(item => item is not null).Cast<ReviewFinding>().ToArray()
                : [];
            var opportunities = TryGetProperty(document.RootElement, "opportunities", out var opportunitiesNode) &&
                                opportunitiesNode.ValueKind == JsonValueKind.Array
                ? opportunitiesNode.EnumerateArray().Select(MapOpportunity).Where(item => item is not null).Cast<ReviewOpportunityItem>().ToArray()
                : [];

            return new ParsedDeepSeekReview(findings, opportunities);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReviewFinding? MapFinding(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = GetString(element, "file", "path") ?? string.Empty;
        var title = GetString(element, "title") ?? string.Empty;
        var description = GetString(element, "description", "risk") ?? string.Empty;
        var suggestion = GetString(element, "suggestion", "fix") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(file) ||
            string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var startLine = GetInt(element, "start_line", "startLine", "line") ?? 0;
        var endLine = GetInt(element, "end_line", "endLine") ?? startLine;
        return new ReviewFinding(
            file.Trim(),
            GetString(element, "line_hint", "lineHint") ?? (startLine > 0 ? startLine.ToString() : string.Empty),
            ParseCategory(GetString(element, "category")),
            ParseSeverity(GetString(element, "severity")),
            ReviewFindingSource.ExternalReview,
            title.Trim(),
            description.Trim(),
            GetString(element, "existing_code", "existingCode", "code")?.Trim() ?? string.Empty,
            suggestion.Trim(),
            startLine,
            endLine);
    }

    private static ReviewOpportunityItem? MapOpportunity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = GetString(element, "file", "path") ?? string.Empty;
        var title = GetString(element, "title") ?? string.Empty;
        var description = GetString(element, "description") ?? string.Empty;
        var suggestion = GetString(element, "suggestion") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(file) ||
            string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var startLine = GetInt(element, "start_line", "startLine", "line") ?? 0;
        return new ReviewOpportunityItem(
            file.Trim(),
            GetString(element, "line_hint", "lineHint") ?? (startLine > 0 ? startLine.ToString() : string.Empty),
            title.Trim(),
            description.Trim(),
            suggestion.Trim(),
            startLine);
    }

    private static FindingCategory ParseCategory(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<FindingCategory>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            "correctness" => FindingCategory.Bug,
            "maintainability" => FindingCategory.CodeStyle,
            "style" => FindingCategory.CodeStyle,
            "testcoverage" => FindingCategory.Reliability,
            "test_coverage" => FindingCategory.Reliability,
            "concurrency" => FindingCategory.Reliability,
            "async" => FindingCategory.Reliability,
            "auth" => FindingCategory.Security,
            _ => FindingCategory.Bug
        };
    }

    private static FindingSeverity ParseSeverity(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<FindingSeverity>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            "blocker" => FindingSeverity.Critical,
            "major" => FindingSeverity.High,
            "minor" => FindingSeverity.Low,
            "info" => FindingSeverity.Low,
            _ => FindingSeverity.Medium
        };
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out var property))
            {
                return property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Number => property.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            {
                return value;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), out var stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record PreparedDeepSeekWorkspace(
        string RunDirectory,
        string WorkingDirectory,
        string Prompt,
        string PromptPath,
        string MetadataPath,
        string DiffPath,
        string? RepositoryWorkspace,
        IReadOnlyList<string> ChangedFiles,
        string RiskDomainsSummary);

    public sealed record ParsedDeepSeekReview(
        IReadOnlyList<ReviewFinding> Findings,
        IReadOnlyList<ReviewOpportunityItem> Opportunities);

    private sealed class StreamProgressTracker(string engineName)
    {
        private const int MinProgressPercent = 51;
        private const int MaxProgressPercent = 70;
        private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(8);
        private DateTimeOffset _lastPublishedAt = DateTimeOffset.MinValue;
        private string _lastMessage = string.Empty;
        private int _eventCount;
        private int _toolEventCount;

        public DeepSeekTuiReviewProgress? TryBuild(string line)
        {
            var description = DescribeProgressLine(line);
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            _eventCount++;
            if (description.Contains("инструмент", StringComparison.OrdinalIgnoreCase))
            {
                _toolEventCount++;
            }

            var message = $"{engineName}: {description}";
            var now = DateTimeOffset.UtcNow;
            if (message.Equals(_lastMessage, StringComparison.Ordinal) &&
                now - _lastPublishedAt < _minInterval)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(_lastMessage) &&
                now - _lastPublishedAt < _minInterval)
            {
                return null;
            }

            _lastMessage = message;
            _lastPublishedAt = now;
            var progress = Math.Clamp(
                MinProgressPercent + _toolEventCount * 2 + _eventCount / 8,
                MinProgressPercent,
                MaxProgressPercent);
            return new DeepSeekTuiReviewProgress(message, progress, description);
        }
    }
}
