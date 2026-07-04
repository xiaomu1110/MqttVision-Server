using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Infrastructure.Storage;

public interface IDetectionStorage
{
    Task<ImageUploadResponse> SaveSourceImageAsync(
        string taskId,
        IFormFile image,
        string publicBaseUrl,
        CancellationToken cancellationToken);

    Task<SourceImageFile?> FindSourceImageAsync(
        string taskId,
        UploadedImageReference image,
        string publicBaseUrl,
        CancellationToken cancellationToken);

    DetectionTaskWorkspace CreateTaskWorkspace(string taskId);

    Task SaveTaskRecordAsync(DetectionTaskRecord record, CancellationToken cancellationToken);

    Task SaveJsonAsync(string path, object value, CancellationToken cancellationToken);

    Task SaveTextAsync(string path, string content, CancellationToken cancellationToken);

    string BuildPublicFileUrl(string path, string publicBaseUrl);
}
