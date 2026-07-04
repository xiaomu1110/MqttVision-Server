namespace MqttVision.Server.Contracts;

public sealed class DetectionResultMessage
{
    public string SchemaVersion { get; init; } = "1.0";

    public string MessageType { get; init; } = "DetectionTaskResult";

    public string TaskId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string SiteId { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DetectionResultSummary Summary { get; init; } = new();

    public string? ResultJsonUrl { get; init; }

    public string? ReportUrl { get; init; }

    public string? VisualSummaryUrl { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class DetectionResultSummary
{
    public int TerminalCount { get; init; }

    public int WireTagCount { get; init; }

    public int PairCount { get; init; }

    public int CorrectPairCount { get; init; }

    public int SuspectedErrorCount { get; init; }

    public int EmptyTerminalCount { get; init; }

    public int OcrItemCount { get; init; }

    public int ConfigurationMatchedCount { get; init; }

    public int ConfigurationMismatchCount { get; init; }

    public int ConfigurationUnrecognizedCount { get; init; }

    public bool ModelIntegrationPending { get; init; }
}
