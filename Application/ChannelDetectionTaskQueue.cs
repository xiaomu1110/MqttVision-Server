using System.Threading.Channels;

namespace MqttVision.Server.Application;

public sealed class ChannelDetectionTaskQueue : IDetectionTaskQueue
{
    private readonly Channel<DetectionTaskWorkItem> queue =
        Channel.CreateUnbounded<DetectionTaskWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask QueueAsync(DetectionTaskWorkItem item, CancellationToken cancellationToken) =>
        queue.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<DetectionTaskWorkItem> DequeueAllAsync(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);
}
