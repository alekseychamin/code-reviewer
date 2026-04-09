namespace TfsReviewPlatform.Application.Models;

public sealed record ReviewWorkspaceToolRequest(
    string ToolName,
    string Reason,
    string Query = "",
    string FilePath = "",
    string PathScope = "",
    int StartLine = 1,
    int MaxLines = 120);
