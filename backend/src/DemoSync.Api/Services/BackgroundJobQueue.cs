using System.Threading.Channels;

namespace DemoSync.Api.Services;

public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(sessionId, cancellationToken);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
