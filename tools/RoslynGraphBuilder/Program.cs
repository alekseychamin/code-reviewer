using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

static string NormalizePath(string path) =>
    Path.GetFullPath(path).Replace('\\', '/').Trim();

static string FindGitRoot(string startPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(startPath)) ?? Path.GetFullPath(startPath);
    while (true)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            return dir.Replace('\\', '/');
        }

        var parent = Directory.GetParent(dir);
        if (parent is null)
        {
            return Path.GetDirectoryName(Path.GetFullPath(startPath))!.Replace('\\', '/');
        }

        dir = parent.FullName;
    }
}

static bool ParseBoolArg(string[] args, string name, bool defaultValue)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(args[i][(name.Length + 1)..], out var parsed))
        {
            return parsed;
        }
    }

    return defaultValue;
}

static int ParseIntArg(string[] args, string name, int defaultValue)
{
    foreach (var arg in args)
    {
        var prefix = name + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(arg[prefix.Length..], out var parsed))
        {
            return parsed;
        }
    }

    return defaultValue;
}

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "Usage: RoslynGraphBuilder <solution.sln> <changed-files.txt> <out.json> [--changed-only=true] [--with-refs] [--max-references=50]");
    return 1;
}

var solutionPath = NormalizePath(args[0]);
var changedFilesPath = NormalizePath(args[1]);
var outputPath = NormalizePath(args[2]);
var changedOnly = ParseBoolArg(args, "--changed-only", true);
var withRefs = ParseBoolArg(args, "--with-refs", false);
var maxReferences = ParseIntArg(args, "--max-references", 50);

var workspaceRoot = FindGitRoot(solutionPath).Replace('\\', '/');

var changedFiles = File.ReadAllLines(changedFilesPath)
    .Where(static x => !string.IsNullOrWhiteSpace(x) && x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
    .Select(static x => x.Replace('\\', '/').Trim())
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

MSBuildLocator.RegisterDefaults();

using var msbuildWorkspace = MSBuildWorkspace.Create();
msbuildWorkspace.WorkspaceFailed += (_, e) => Console.Error.WriteLine($"Workspace warning: {e.Diagnostic}");

Console.WriteLine($"Opening solution: {solutionPath}");
var solution = await msbuildWorkspace.OpenSolutionAsync(solutionPath);

var graph = new CodeGraph();
var changedProjects = changedOnly
    ? solution.Projects
        .Where(p => p.Documents.Any(d =>
            d.FilePath is not null &&
            changedFiles.Contains(GetRepoRelativePath(workspaceRoot, d.FilePath))))
        .ToImmutableHashSet()
    : solution.Projects.ToImmutableHashSet();

foreach (var project in solution.Projects)
{
    if (!changedProjects.Contains(project))
    {
        continue;
    }

    Console.WriteLine($"Project: {project.Name}");

    foreach (var document in project.Documents)
    {
        if (document.FilePath is null)
        {
            continue;
        }

        var normalizedDocPath = GetRepoRelativePath(workspaceRoot, document.FilePath);
        if (!changedFiles.Contains(normalizedDocPath) &&
            !changedFiles.Any(f => normalizedDocPath.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        Console.WriteLine($"Changed document: {document.FilePath}");

        var root = await document.GetSyntaxRootAsync();
        var semanticModel = await document.GetSemanticModelAsync();
        if (root is null || semanticModel is null)
        {
            continue;
        }

        var fileNode = Node.File(document.FilePath, workspaceRoot);
        graph.AddNode(fileNode);

        foreach (var typeDeclaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
            if (typeSymbol is null)
            {
                continue;
            }

            var typeNode = Node.Symbol(typeSymbol, workspaceRoot);
            graph.AddNode(typeNode);
            graph.AddEdge(fileNode.Id, typeNode.Id, "DECLARES");

            AddInheritanceEdges(graph, typeSymbol, workspaceRoot);
            AddInterfaceEdges(graph, typeSymbol, workspaceRoot);

            foreach (var methodDeclaration in typeDeclaration.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration) as IMethodSymbol;
                if (methodSymbol is null)
                {
                    continue;
                }

                var methodNode = Node.Symbol(methodSymbol, workspaceRoot);
                graph.AddNode(methodNode);
                graph.AddEdge(typeNode.Id, methodNode.Id, "CONTAINS");

                AddCallEdges(graph, semanticModel, methodSymbol, methodDeclaration, workspaceRoot);
            }

            if (withRefs)
            {
                await AddReferenceEdgesForType(graph, solution, typeSymbol, workspaceRoot, maxReferences);
            }
        }
    }
}

var serializeOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var nodeDtos = graph.Nodes.Values
    .Select(n => new { n.Id, n.Kind, n.Name, n.FilePath, n.Line, n.AssemblyName })
    .ToList();
var edgeDtos = graph.Edges
    .Select(e => new { from = e.From, to = e.To, kind = e.Kind })
    .ToList();

var json = JsonSerializer.Serialize(new { nodes = nodeDtos, edges = edgeDtos }, serializeOptions);

await File.WriteAllTextAsync(outputPath, json);
Console.WriteLine($"Graph written to: {outputPath}");
return 0;

static string GetRepoRelativePath(string workspaceRoot, string fullPath)
{
    var root = workspaceRoot.Replace('\\', '/').TrimEnd('/');
    var full = Path.GetFullPath(fullPath).Replace('\\', '/');
    if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
    {
        return full[(root.Length + 1)..];
    }

    return Path.GetFileName(fullPath);
}

static void AddInheritanceEdges(CodeGraph graph, INamedTypeSymbol typeSymbol, string workspaceRoot)
{
    if (typeSymbol.BaseType is null || typeSymbol.BaseType.SpecialType == SpecialType.System_Object)
    {
        return;
    }

    var baseNode = Node.Symbol(typeSymbol.BaseType, workspaceRoot);
    graph.AddNode(baseNode);
    graph.AddEdge(Node.Symbol(typeSymbol, workspaceRoot).Id, baseNode.Id, "INHERITS");
}

static void AddInterfaceEdges(CodeGraph graph, INamedTypeSymbol typeSymbol, string workspaceRoot)
{
    var typeNode = Node.Symbol(typeSymbol, workspaceRoot);

    foreach (var iface in typeSymbol.Interfaces)
    {
        var ifaceNode = Node.Symbol(iface, workspaceRoot);
        graph.AddNode(ifaceNode);
        graph.AddEdge(typeNode.Id, ifaceNode.Id, "IMPLEMENTS");
    }
}

static void AddCallEdges(
    CodeGraph graph,
    SemanticModel semanticModel,
    IMethodSymbol currentMethod,
    MethodDeclarationSyntax methodDeclaration,
    string workspaceRoot)
{
    var currentMethodNode = Node.Symbol(currentMethod, workspaceRoot);
    graph.AddNode(currentMethodNode);

    foreach (var invocation in methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var targetSymbol = symbolInfo.Symbol as IMethodSymbol;
        if (targetSymbol is null)
        {
            continue;
        }

        var targetNode = Node.Symbol(targetSymbol, workspaceRoot);
        graph.AddNode(targetNode);
        graph.AddEdge(currentMethodNode.Id, targetNode.Id, "CALLS");
    }

    foreach (var objectCreation in methodDeclaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
    {
        var symbolInfo = semanticModel.GetSymbolInfo(objectCreation);
        var ctorSymbol = symbolInfo.Symbol as IMethodSymbol;
        if (ctorSymbol is null)
        {
            continue;
        }

        var ctorNode = Node.Symbol(ctorSymbol, workspaceRoot);
        graph.AddNode(ctorNode);
        graph.AddEdge(currentMethodNode.Id, ctorNode.Id, "CALLS_CTOR");
    }
}

static async Task AddReferenceEdgesForType(
    CodeGraph graph,
    Solution solution,
    INamedTypeSymbol typeSymbol,
    string workspaceRoot,
    int maxReferences)
{
    var symbolNode = Node.Symbol(typeSymbol, workspaceRoot);
    graph.AddNode(symbolNode);

    var references = await SymbolFinder.FindReferencesAsync(typeSymbol, solution);
    var count = 0;
    foreach (var referencedSymbol in references)
    {
        foreach (var location in referencedSymbol.Locations)
        {
            if (count >= maxReferences)
            {
                return;
            }

            var document = solution.GetDocument(location.Document.Id);
            if (document?.FilePath is null)
            {
                continue;
            }

            var lineSpan = location.Location.GetLineSpan();
            var line = lineSpan.StartLinePosition.Line + 1;

            var locationNode = Node.Location(document.FilePath, line, workspaceRoot);
            graph.AddNode(locationNode);
            graph.AddEdge(symbolNode.Id, locationNode.Id, "REFERENCED_BY");
            count++;
        }
    }
}

public sealed class CodeGraph
{
    public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);
    public List<Edge> Edges { get; } = [];

    public void AddNode(Node node) => Nodes.TryAdd(node.Id, node);

    public void AddEdge(string from, string to, string kind) => Edges.Add(new Edge(from, to, kind));
}

public sealed record Node(
    string Id,
    string Kind,
    string Name,
    string? FilePath,
    int? Line,
    string? AssemblyName)
{
    public static Node File(string path, string workspaceRoot)
    {
        var normalized = path.Replace('\\', '/');
        return new Node(
            Id: $"file:{normalized}",
            Kind: "File",
            Name: Path.GetFileName(path),
            FilePath: GetRepoRelativePathStatic(workspaceRoot, path),
            Line: null,
            AssemblyName: null);
    }

    public static Node Location(string path, int line, string workspaceRoot)
    {
        var normalized = path.Replace('\\', '/');
        return new Node(
            Id: $"loc:{normalized}:{line}",
            Kind: "Location",
            Name: $"{Path.GetFileName(path)}:{line}",
            FilePath: GetRepoRelativePathStatic(workspaceRoot, path),
            Line: line,
            AssemblyName: null);
    }

    public static Node Symbol(ISymbol symbol, string workspaceRoot)
    {
        var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var id = $"sym:{symbol.GetDocumentationCommentId()}";
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        string? rel = null;
        int? line = null;
        if (loc?.SourceTree?.FilePath is { } fp)
        {
            rel = GetRepoRelativePathStatic(workspaceRoot, fp);
            line = loc.GetLineSpan().StartLinePosition.Line + 1;
        }

        return new Node(
            Id: string.IsNullOrEmpty(symbol.GetDocumentationCommentId()) ? $"sym:{display}" : id,
            Kind: symbol.Kind.ToString(),
            Name: display,
            FilePath: rel,
            Line: line,
            AssemblyName: symbol.ContainingAssembly?.Name);
    }

    private static string GetRepoRelativePathStatic(string workspaceRoot, string fullPath)
    {
        var root = workspaceRoot.Replace('\\', '/').TrimEnd('/');
        var full = Path.GetFullPath(fullPath).Replace('\\', '/');
        if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return full[(root.Length + 1)..];
        }

        return Path.GetFileName(fullPath);
    }
}

public sealed record Edge(string From, string To, string Kind);
