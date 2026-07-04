namespace MqttVision.Server.Contracts;

public sealed class DetectionTaskMessage
{
    public string SchemaVersion { get; init; } = "1.0";

    public string MessageType { get; init; } = string.Empty;

    public string TaskId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string OperatorId { get; init; } = string.Empty;

    public string SiteId { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public UploadedImageReference Image { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class UploadedImageReference
{
    public string TransferMode { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;
}
