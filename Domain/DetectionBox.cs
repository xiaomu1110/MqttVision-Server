namespace MqttVision.Server.Domain;

public sealed record DetectionBox(
    float X,
    float Y,
    float Width,
    float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float CenterX => X + Width / 2;

    public float CenterY => Y + Height / 2;
}
