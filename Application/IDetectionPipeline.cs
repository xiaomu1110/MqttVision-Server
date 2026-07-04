using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public interface IDetectionPipeline
{
    Task<DetectionPipelineResult> ProcessAsync(
        DetectionTaskRecord record,
        CancellationToken cancellationToken);
}
