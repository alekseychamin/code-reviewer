using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Application.Models.Graph;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IGraphAwareChunker
{
    Task<PreprocessedDiff> AugmentWithGraphChunksAsync(
        PreprocessedDiff preprocessed,
        CodeGraph? graph,
        string workspaceRoot,
        ReviewPipelineOptions pipelineOptions,
        CancellationToken cancellationToken);
}
