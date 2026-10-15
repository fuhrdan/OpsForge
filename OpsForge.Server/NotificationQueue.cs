using System.Threading.Channels;

namespace OpsForge.Server;

public sealed class NotificationQueue
{
    private readonly Channel<NotificationEnvelope> _channel =
        Channel.CreateBounded<NotificationEnvelope>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public bool Enqueue(NotificationEnvelope envelope) => _channel.Writer.TryWrite(envelope);

    public IAsyncEnumerable<NotificationEnvelope> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
