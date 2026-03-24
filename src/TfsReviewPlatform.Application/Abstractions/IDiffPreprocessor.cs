using TfsReviewPlatform.Application.Models;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IDiffPreprocessor
{
    PreprocessedDiff Process(string diffText);
}
