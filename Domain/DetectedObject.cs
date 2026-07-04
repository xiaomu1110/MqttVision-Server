namespace MqttVision.Server.Domain;

public sealed class DetectedObject
{
    public int Id { get; set; }

    public int ClassId { get; init; }

    public string ClassName { get; init; } = string.Empty;

    public float Confidence { get; init; }

    public DetectionBox Box { get; init; } = new(0, 0, 0, 0);

    public string? CropPath { get; set; }

    public string? CropRelativePath { get; set; }

    public string? CropUrl { get; set; }
}
