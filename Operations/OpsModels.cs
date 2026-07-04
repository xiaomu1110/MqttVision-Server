namespace MqttVision.Server.Operations;

public sealed class OpsDashboardSnapshot
{
    public DateTimeOffset ServerTime { get; init; } = DateTimeOffset.Now;

    public OpsCounters Counters { get; init; } = new();

    public OpsComponentState MqttSubscriber { get; init; } = OpsComponentState.Unknown("MQTT Subscriber");

    public OpsComponentState MqttPublisher { get; init; } = OpsComponentState.Unknown("MQTT Publisher");

    public OpsComponentState OcrService { get; init; } = OpsComponentState.Unknown("PaddleOCR Serving");

    public OpsComponentState YoloModel { get; init; } = OpsComponentState.Unknown("YOLO ONNX");

    public string StorageRoot { get; init; } = string.Empty;

    public string ArchiveSizeText { get; init; } = "0 B";

    public string YoloModelPath { get; init; } = string.Empty;

    public string OcrServiceUrl { get; init; } = string.Empty;

    public IReadOnlyList<OpsTaskRow> RecentTasks { get; init; } = Array.Empty<OpsTaskRow>();

    public IReadOnlyList<OpsAlertItem> Alerts { get; init; } = Array.Empty<OpsAlertItem>();
}

public sealed class OpsCounters
{
    public int TodayTasks { get; init; }

    public int CompletedTasks { get; init; }

    public int FailedTasks { get; init; }

    public int ActiveTasks { get; init; }

    public int MatchedCount { get; init; }

    public int MismatchCount { get; init; }

    public int UnrecognizedCount { get; init; }

    public int QueuedTasks { get; init; }
}

public sealed class OpsComponentState
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = "unknown";

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public static OpsComponentState Unknown(string name) =>
        new()
        {
            Name = name,
            Status = "unknown",
            Message = "尚未收到运行状态。"
        };
}

public sealed class OpsTaskRow
{
    public string TaskId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string SiteId { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public string Stage { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public int TerminalCount { get; init; }

    public int WireTagCount { get; init; }

    public int MatchedCount { get; init; }

    public int MismatchCount { get; init; }

    public int UnrecognizedCount { get; init; }

    public string? ResultJsonUrl { get; init; }

    public string? ReportUrl { get; init; }

    public string? VisualSummaryUrl { get; init; }
}

public sealed class OpsAlertItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Level { get; init; } = "info";

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? TaskId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
