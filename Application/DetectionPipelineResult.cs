using MqttVision.Server.Contracts;

namespace MqttVision.Server.Application;

public sealed record DetectionPipelineResult(
    bool Success,
    string Status,
    string Message,
    DetectionResultSummary Summary,
    string? ResultJsonUrl,
    string? ReportUrl,
    string? VisualSummaryUrl,
    string? ErrorMessage)
{
    public static DetectionPipelineResult Failed(string message) =>
        new(
            false,
            "Failed",
            message,
            new DetectionResultSummary(),
            null,
            null,
            null,
            message);
}
