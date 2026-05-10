namespace TfsReviewPlatform.Domain.Entities;

public sealed class SemanticCodeContextArtifact
{
    public static SemanticCodeContextArtifact Empty { get; } = new();

    public bool Enabled { get; init; }

    public bool Attempted { get; init; }

    public bool Succeeded { get; init; }

    public bool TimedOut { get; init; }

    public bool CacheReuseEnabled { get; init; }

    public bool SourceCacheHit { get; init; }

    public bool TargetCacheHit { get; init; }

    public int SourceFilesSelected { get; init; }

    public int TargetFilesSelected { get; init; }

    public int SourceFilesIndexed { get; init; }

    public int TargetFilesIndexed { get; init; }

    public int SourceChunksIndexed { get; init; }

    public int TargetChunksIndexed { get; init; }

    public int SourceFilesMissing { get; init; }

    public int TargetFilesMissing { get; init; }

    public int SourceFilesTooLarge { get; init; }

    public int TargetFilesTooLarge { get; init; }

    public int SourceFilesEmpty { get; init; }

    public int TargetFilesEmpty { get; init; }

    public int SourceFilesWithoutChunks { get; init; }

    public int TargetFilesWithoutChunks { get; init; }

    public int SourceFilesReadFailed { get; init; }

    public int TargetFilesReadFailed { get; init; }

    public int SourceFilesSkippedDeleted { get; init; }

    public int TargetFilesSkippedAdded { get; init; }

    public int TargetBaselineFilesSelected { get; init; }

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int SnippetCount { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public string Status { get; init; } = "not_attempted";

    public string Message { get; init; } = string.Empty;

    public string? SourceCommitSha { get; init; }

    public string? TargetCommitSha { get; init; }

    public IReadOnlyList<SemanticCodeContextSnippetArtifact> Snippets { get; init; } = [];
}

public sealed class SemanticCodeContextSnippetArtifact
{
    public string RevisionKind { get; init; } = string.Empty;

    public string CommitSha { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public double Score { get; init; }

    public string Query { get; init; } = string.Empty;
}
