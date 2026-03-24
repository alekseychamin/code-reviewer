using System.Threading.Channels;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Application.Abstractions;

public interface IReviewProgressStore
{
    void EnsureRun(Guid runId);

    ChannelReader<ReviewProgressUpdate> Subscribe(Guid runId);

    ValueTask PublishAsync(ReviewProgressUpdate update, CancellationToken cancellationToken);

    void Complete(Guid runId);
}
