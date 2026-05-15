namespace TfsReviewPlatform.Application.Models;

public sealed class DeepSeekTuiReviewOptions
{
    public const string SectionName = "DeepSeekTuiReview";

    public bool Enabled { get; init; }

    public bool UseAsPrimaryReviewer { get; init; }

    public string EngineName { get; init; } = "DeepSeek-TUI";

    public string ExecutablePath { get; init; } = "deepseek";

    public string WorkspaceRoot { get; init; } = "/review-workspaces";

    public string RunDirectoryName { get; init; } = "runs";

    public bool CopyRepositoryToWorkspace { get; init; } = true;

    public bool UsePersistentRepositoryWorkspace { get; init; } = true;

    public string RepositoryDirectoryName { get; init; } = "repositories";

    public int TimeoutSeconds { get; init; } = 900;

    public string Model { get; init; } = "auto";

    public bool AutoApproveTools { get; init; } = true;

    public string ApiKeyEnvironmentVariable { get; init; } = "DEEPSEEK_API_KEY";

    public int MaxStdoutCharacters { get; init; } = 300_000;

    public int MaxStderrCharacters { get; init; } = 80_000;

    public IReadOnlyList<string> ExcludedRepositoryDirectories { get; init; } =
    [
        ".git",
        "bin",
        "obj",
        "node_modules",
        ".vs",
        ".idea",
        ".vscode"
    ];
}
