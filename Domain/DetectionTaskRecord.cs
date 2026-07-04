using MqttVision.Server.Contracts;

namespace MqttVision.Server.Domain;

public sealed class DetectionTaskRecord
{
    public string TaskId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string OperatorId { get; init; } = string.Empty;

    public string SiteId { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public DetectionTaskStatus Status { get; set; } = DetectionTaskStatus.Created;

    public UploadedImageReference Image { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string? RawMessage { get; init; }

    public string? ResultJsonUrl { get; set; }

    public string? ReportUrl { get; set; }

    public string? VisualSummaryUrl { get; set; }

    public string? ErrorMessage { get; set; }
}
