namespace MqttVision.Server.Domain;

public enum DetectionTaskStatus
{
    Created,
    ImageReceived,
    MqttSubmitted,
    Queued,
    Processing,
    Completed,
    Failed
}
