using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Integrations.Git;

namespace TfsReviewPlatform.Integrations.Roslyn;

public sealed class RoslynWorkspaceBootstrapper(
    ShellGitCommandRunner gitCommandRunner,
    IOptions<ReviewPipelineOptions> reviewPipelineOptions,
    ILogger<RoslynWorkspaceBootstrapper> logger)
    : IRoslynWorkspaceBootstrapper
{
    public async Task<RoslynWorkspaceBootstrapResult> TryPrepareAsync(
        Guid runId,
        DiffAcquisitionResult diffResult,
        string? pullRequestAccessToken,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var roslyn = reviewPipelineOptions.Value.Roslyn;
        var workspacesRoot = string.IsNullOrWhiteSpace(roslyn.WorkspacesRoot)
            ? Path.Combine(Path.GetTempPath(), "tfs-review-platform", "roslyn")
            : roslyn.WorkspacesRoot;
        if (!roslyn.Enabled)
        {
            return new RoslynWorkspaceBootstrapResult(false, null, null, null, null, "Roslyn graph pipeline disabled.");
        }

        if (string.IsNullOrWhiteSpace(diffResult.RepositoryPath) &&
            string.IsNullOrWhiteSpace(diffResult.RepositoryRemoteUrl))
        {
            return new RoslynWorkspaceBootstrapResult(false, null, null, null, null, "No repository information for Roslyn workspace.");
        }

        var workspaceDir = Path.Combine(workspacesRoot, runId.ToString("N"));
        string? solutionPath = null;
        string? changedFilesPath = null;
        Directory.CreateDirectory(workspacesRoot);

        try
        {
            if (Directory.Exists(workspaceDir))
            {
                Directory.Delete(workspaceDir, true);
            }

            var parent = Directory.GetParent(workspaceDir)!.FullName;
            var leaf = Path.GetFileName(workspaceDir);

            if (!string.IsNullOrWhiteSpace(diffResult.RepositoryRemoteUrl))
            {
                var cloneArgs = new List<string>();
                if (!string.IsNullOrWhiteSpace(diffResult.GitHttpExtraHeader))
                {
                    cloneArgs.Add("-c");
                    cloneArgs.Add($"http.extraHeader={diffResult.GitHttpExtraHeader}");
                }

                cloneArgs.AddRange(["clone", "--no-checkout", "--depth", "200", diffResult.RepositoryRemoteUrl, leaf]);
                await gitCommandRunner.RunAsync(parent, cloneArgs, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(diffResult.RepositoryPath))
            {
                var localRepo = Path.GetFullPath(diffResult.RepositoryPath);
                await gitCommandRunner.RunAsync(parent, ["clone", "--no-checkout", localRepo, leaf], cancellationToken);
            }
            else
            {
                return new RoslynWorkspaceBootstrapResult(false, null, null, null, null, "Missing remote or local repository path.");
            }

            if (string.IsNullOrWhiteSpace(diffResult.GitFetchTargetRef) ||
                string.IsNullOrWhiteSpace(diffResult.GitFetchSourceRef))
            {
                return new RoslynWorkspaceBootstrapResult(false, workspaceDir, null, null, workspaceDir, "Missing git fetch ref specs.");
            }

            // Clone may use -c http.extraHeader; fetch does not inherit it for this process. Mirror pull-request providers.
            if (!string.IsNullOrWhiteSpace(diffResult.GitHttpExtraHeader))
            {
                await gitCommandRunner.RunAsync(
                    workspaceDir,
                    ["config", "http.extraHeader", diffResult.GitHttpExtraHeader],
                    cancellationToken);
            }

            await gitCommandRunner.RunAsync(
                workspaceDir,
                ["fetch", "origin", $"{diffResult.GitFetchTargetRef}:refs/remotes/origin/__target__", "--depth=200"],
                cancellationToken);
            await gitCommandRunner.RunAsync(
                workspaceDir,
                ["fetch", "origin", $"{diffResult.GitFetchSourceRef}:refs/remotes/origin/__source__", "--depth=200"],
                cancellationToken);
            await gitCommandRunner.RunAsync(workspaceDir, ["checkout", "refs/remotes/origin/__source__"], cancellationToken);

            changedFilesPath = Path.Combine(workspaceDir, "changed-files.txt");
            var lines = changedFiles
                .Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(static f => f.Replace('\\', '/').Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await File.WriteAllLinesAsync(changedFilesPath, lines, cancellationToken);

            solutionPath = await FindSolutionPathAsync(workspaceDir, lines, cancellationToken);
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                return new RoslynWorkspaceBootstrapResult(false, workspaceDir, null, changedFilesPath, workspaceDir, "No .sln found in workspace.");
            }

            await RunDotnetRestoreAsync(workspaceDir, solutionPath, roslyn.RestoreTimeoutSeconds, cancellationToken);

            return new RoslynWorkspaceBootstrapResult(true, workspaceDir, solutionPath, changedFilesPath, workspaceDir, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Roslyn workspace bootstrap failed for run {RunId}", runId);
            try
            {
                if (Directory.Exists(workspaceDir))
                {
                    Directory.Delete(workspaceDir, true);
                }
            }
            catch
            {
                // ignored
            }

            return new RoslynWorkspaceBootstrapResult(
                false,
                workspaceDir,
                solutionPath,
                changedFilesPath,
                workspaceDir,
                exception.Message);
        }
    }

    private async Task<string?> FindSolutionPathAsync(string workspaceDir, IReadOnlyList<string> changedFiles, CancellationToken cancellationToken)
    {
        try
        {
            var output = await gitCommandRunner.RunAsync(workspaceDir, ["ls-files", "--", "*.sln"], cancellationToken);
            var candidates = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static l => l.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                .Select(l => Path.GetFullPath(Path.Combine(workspaceDir, l.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length == 0 || changedFiles.Count == 0)
            {
                return candidates.FirstOrDefault();
            }

            var firstFile = changedFiles[0].Replace('\\', '/');
            var best = candidates
                .OrderByDescending(sln =>
                {
                    var relDir = Path.GetDirectoryName(Path.GetRelativePath(Path.GetDirectoryName(sln)!, Path.Combine(workspaceDir, firstFile))) ?? string.Empty;
                    return relDir.Length;
                })
                .FirstOrDefault();

            return best ?? candidates.FirstOrDefault();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "git ls-files for *.sln failed");
            return Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    private static async Task RunDotnetRestoreAsync(
        string workingDirectory,
        string solutionPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var restoreCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        restoreCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds)));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(solutionPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet restore.");
        var stdoutTail = new BoundedLineBuffer(120);
        var stderrTail = new BoundedLineBuffer(120);
        var stdoutTask = DrainStreamAsync(process.StandardOutput, stdoutTail, restoreCts.Token);
        var stderrTask = DrainStreamAsync(process.StandardError, stderrTail, restoreCts.Token);

        try
        {
            await process.WaitForExitAsync(restoreCts.Token);
        }
        catch (OperationCanceledException) when (restoreCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
                // ignored
            }

            await Task.WhenAll(stdoutTask, stderrTask);

            throw new TimeoutException(
                $"dotnet restore timed out after {Math.Max(30, timeoutSeconds)} seconds for '{solutionPath}'. " +
                $"stdout tail:\n{stdoutTail.Build()} \n\nstderr tail:\n{stderrTail.Build()}");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet restore failed ({process.ExitCode}) for '{solutionPath}'. " +
                $"stdout tail:\n{stdoutTail.Build()} \n\nstderr tail:\n{stderrTail.Build()}");
        }
    }

    private static async Task DrainStreamAsync(StreamReader reader, BoundedLineBuffer buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            buffer.Add(line);
        }
    }

    private sealed class BoundedLineBuffer(int capacity)
    {
        private readonly Queue<string> lines = new(Math.Max(1, capacity));

        public void Add(string line)
        {
            if (lines.Count >= capacity)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        public string Build()
        {
            if (lines.Count == 0)
            {
                return "<empty>";
            }

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
