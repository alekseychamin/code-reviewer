namespace TfsReviewPlatform.Application.Models;

public sealed class RoslynGraphPipelineOptions
{
    public const string SectionKey = "Roslyn";

    public bool Enabled { get; init; } = true;

    /// <summary>Root directory for per-run cloned workspaces.</summary>
    public string WorkspacesRoot { get; init; } = Path.Combine(Path.GetTempPath(), "tfs-review-platform", "roslyn");

    /// <summary>
    /// Path to RoslynGraphBuilder published DLL, or <c>dotnet</c> args prefix. When null, resolved from application base directory.
    /// </summary>
    public string? GraphBuilderDllPath { get; init; }

    public int RestoreTimeoutSeconds { get; init; } = 300;

    public int GraphTimeoutSeconds { get; init; } = 240;

    public int MaxNeighborsPerAnchor { get; init; } = 24;

    public int MaxNeighborSnippetCharacters { get; init; } = 1200;

    /// <summary>Max reference locations per type symbol when building REFERENCED_BY edges.</summary>
    public int MaxReferencesPerSymbol { get; init; } = 50;

    /// <summary>
    /// When true, RoslynGraphBuilder runs with <c>--with-refs</c> (FindReferences for types in changed files).
    /// GraphAwareChunker then adds reference-site snippets for the containing type (method anchors) or the type itself.
    /// </summary>
    public bool IncludeReferenceEdges { get; init; } = false;
}
