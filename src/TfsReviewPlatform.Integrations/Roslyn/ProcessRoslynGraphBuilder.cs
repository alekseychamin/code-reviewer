using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Models.Graph;

namespace TfsReviewPlatform.Integrations.Roslyn;

public sealed class ProcessRoslynGraphBuilder(
    IOptions<ReviewPipelineOptions> reviewPipelineOptions,
    ILogger<ProcessRoslynGraphBuilder> logger)
    : IRoslynGraphBuilder
{
    public async Task<CodeGraph?> BuildAsync(
        string workspaceDirectory,
        string solutionPath,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var roslyn = reviewPipelineOptions.Value.Roslyn;
        var changedPath = Path.Combine(workspaceDirectory, "changed-files.txt");
        await File.WriteAllLinesAsync(
            changedPath,
            changedFiles
                .Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(static f => f.Replace('\\', '/').Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        var graphOut = Path.Combine(workspaceDirectory, "graph.json");
        var toolDll = ResolveToolDll(roslyn.GraphBuilderDllPath);
        if (!File.Exists(toolDll))
        {
            logger.LogWarning("RoslynGraphBuilder DLL not found at {Path}", toolDll);
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, roslyn.GraphTimeoutSeconds)));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workspaceDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(toolDll);
        psi.ArgumentList.Add(solutionPath);
        psi.ArgumentList.Add(changedPath);
        psi.ArgumentList.Add(graphOut);
        psi.ArgumentList.Add("--changed-only=true");
        psi.ArgumentList.Add($"--max-references={roslyn.MaxReferencesPerSymbol}");
        if (roslyn.IncludeReferenceEdges)
        {
            psi.ArgumentList.Add("--with-refs=true");
        }

        logger.LogInformation(
            "RoslynGraphBuilder starting: IncludeReferenceEdges={IncludeRefs}, toolDll={ToolDll}, solution={Solution}, changedFilesPath={ChangedPath}, graphOut={GraphOut}",
            roslyn.IncludeReferenceEdges,
            toolDll,
            solutionPath,
            changedPath,
            graphOut);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        await process.WaitForExitAsync(timeoutCts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        if (process.ExitCode != 0)
        {
            logger.LogWarning("RoslynGraphBuilder exited {Code}: {Err}", process.ExitCode, stderr);
            return null;
        }

        if (!File.Exists(graphOut))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(graphOut, cancellationToken);
        return CodeGraph.TryParse(json);
    }

    private static string ResolveToolDll(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "roslyn", "RoslynGraphBuilder.dll"),
                     Path.Combine(baseDir, "RoslynGraphBuilder.dll")
                 })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.Combine(baseDir, "roslyn", "RoslynGraphBuilder.dll");
    }
}
