using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public interface IObjectDetector
{
    Task<IReadOnlyList<DetectedObject>> DetectAsync(
        string imagePath,
        CancellationToken cancellationToken);
}
