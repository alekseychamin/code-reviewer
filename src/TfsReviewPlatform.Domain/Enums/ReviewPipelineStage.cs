namespace TfsReviewPlatform.Domain.Enums;

public enum ReviewPipelineStage
{
    DiffAcquisition,
    Preprocessing,
    ChangeDescription,
    ChunkReview,
    FindingsNormalization,
    FinalSynthesis,
    Publish
}
