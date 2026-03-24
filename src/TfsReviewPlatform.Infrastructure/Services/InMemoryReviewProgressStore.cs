using System.Collections.Concurrent;
using System.Threading.Channels;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Infrastructure.Services;

public sealed class InMemoryReviewProgressStore : IReviewProgressStore
{
    private readonly ConcurrentDictionary<Guid, Channel<ReviewProgressUpdate>> _channels = new();

    public void EnsureRun(Guid runId)
    {
        _channels.GetOrAdd(runId, _ => Channel.CreateUnbounded<ReviewProgressUpdate>());
    }

    public ChannelReader<ReviewProgressUpdate> Subscribe(Guid runId)
    {
        EnsureRun(runId);
        return _channels[runId].Reader;
    }

    public async ValueTask PublishAsync(ReviewProgressUpdate update, CancellationToken cancellationToken)
    {
        EnsureRun(update.RunId);
        await _channels[update.RunId].Writer.WriteAsync(update, cancellationToken);
    }

    public void Complete(Guid runId)
    {
        if (_channels.TryGetValue(runId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
