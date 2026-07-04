namespace MqttVision.Server.Contracts;

public sealed class DetectionProgressMessage
{
    public string SchemaVersion { get; init; } = "1.0";

    public string MessageType { get; init; } = "DetectionTaskProgress";

    public string TaskId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string SiteId { get; init; } = string.Empty;

    public string Stage { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
