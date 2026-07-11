using MqttVision.Server.Domain;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Application;

public interface IObjectDetector
{
    Task<IReadOnlyList<DetectedObject>> DetectAsync(
        string imagePath,
        ProcessingOptions processing,
        CancellationToken cancellationToken);
}
