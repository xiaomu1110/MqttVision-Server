using MqttVision.Server.Application;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Infrastructure.Mqtt;

public interface IDetectionResultPublisher
{
    Task PublishProgressAsync(
        DetectionTaskRecord record,
        string stage,
        string message,
        CancellationToken cancellationToken);

    Task PublishResultAsync(
        DetectionTaskRecord record,
        DetectionPipelineResult result,
        CancellationToken cancellationToken);
}
