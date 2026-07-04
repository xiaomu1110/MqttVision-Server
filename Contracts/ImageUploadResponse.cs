namespace MqttVision.Server.Contracts;

public sealed record ImageUploadResponse(
    string TaskId,
    string ImageId,
    string FileName,
    string Url,
    string Sha256,
    long Size,
    string ContentType,
    string RelativePath);
