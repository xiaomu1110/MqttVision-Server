namespace MqttVision.Server.Domain;

public sealed class DetectionPair
{
    public int PairIndex { get; init; }

    public string Category { get; init; } = string.Empty;

    public DetectedObject Terminal { get; init; } = new();

    public DetectedObject? WireMarkerTube { get; init; }

    public double? DistancePixels { get; init; }

    public double? HorizontalDistancePixels { get; init; }

    public double? VerticalGapPixels { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string? FolderPath { get; set; }

    public string? FolderRelativePath { get; set; }

    public string? FolderUrl { get; set; }
}
