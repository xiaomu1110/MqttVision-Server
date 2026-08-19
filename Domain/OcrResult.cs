namespace MqttVision.Server.Domain;

public sealed class OcrResult
{
    public int DetectionId { get; init; }

    public string TargetType { get; init; } = string.Empty;

    public string? ImageRelativePath { get; init; }

    public string? ImageUrl { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? RawText { get; init; }

    public string? NormalizedText { get; init; }

    public double? Confidence { get; init; }

    public int RotationDegrees { get; init; }

    public string? ErrorMessage { get; init; }
}
