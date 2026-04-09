namespace TfsReviewPlatform.Application.Models;

public sealed record ReviewWorkspaceToolResponse(
    string ToolName,
    string Source,
    string Content,
    string FilePath = "",
    int StartLine = 0,
    int EndLine = 0);
