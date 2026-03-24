namespace TfsReviewPlatform.Application.Models;

public sealed class ArtifactDownloadResult
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }
}
