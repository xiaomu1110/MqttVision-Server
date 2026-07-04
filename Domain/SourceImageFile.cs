namespace MqttVision.Server.Domain;

public sealed record SourceImageFile(
    string FilePath,
    string RelativePath,
    string Url,
    string Sha256,
    long Size);
