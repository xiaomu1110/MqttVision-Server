namespace MqttVision.Server.Application;

public interface IDetectionTaskQueue
{
    ValueTask QueueAsync(DetectionTaskWorkItem item, CancellationToken cancellationToken);

    IAsyncEnumerable<DetectionTaskWorkItem> DequeueAllAsync(CancellationToken cancellationToken);
}
