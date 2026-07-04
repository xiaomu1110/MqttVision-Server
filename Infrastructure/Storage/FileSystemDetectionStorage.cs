using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Infrastructure.Storage;

public sealed class FileSystemDetectionStorage : IDetectionStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly MqttVisionServerOptions options;
    private readonly ServerPathInitializer paths;

    public FileSystemDetectionStorage(
        IOptions<MqttVisionServerOptions> options,
        ServerPathInitializer paths)
    {
        this.options = options.Value;
        this.paths = paths;
    }

    public async Task<ImageUploadResponse> SaveSourceImageAsync(
        string taskId,
        IFormFile image,
        string publicBaseUrl,
        CancellationToken cancellationToken)
    {
        if (image.Length <= 0)
        {
            throw new InvalidOperationException("上传图片为空。");
        }

        if (image.Length > options.MaxUploadBytes)
        {
            throw new InvalidOperationException($"上传图片超过限制: {options.MaxUploadBytes} bytes。");
        }

        var extension = Path.GetExtension(image.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var imageId = $"img-{Guid.NewGuid():N}";
        var taskFolder = GetTaskFolder(paths.UploadsRoot, taskId);
        var sourceFolder = Path.Combine(taskFolder, "source");
        Directory.CreateDirectory(sourceFolder);

        var fileName = $"original{extension}";
        var targetPath = Path.Combine(sourceFolder, fileName);

        await using (var input = image.OpenReadStream())
        await using (var output = File.Create(targetPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        var sha256 = await ComputeSha256Async(targetPath, cancellationToken);
        var relativePath = Path.GetRelativePath(paths.StorageRoot, targetPath).Replace('\\', '/');
        var url = $"{publicBaseUrl.TrimEnd('/')}/files/{relativePath}";

        var result = new ImageUploadResponse(
            taskId,
            imageId,
            fileName,
            url,
            sha256,
            image.Length,
            image.ContentType,
            relativePath);

        var metadataFolder = Path.Combine(taskFolder, "metadata");
        Directory.CreateDirectory(metadataFolder);
        await SaveJsonAsync(Path.Combine(metadataFolder, "upload.json"), result, cancellationToken);

        return result;
    }

    public async Task<SourceImageFile?> FindSourceImageAsync(
        string taskId,
        UploadedImageReference image,
        string publicBaseUrl,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        if (TryGetRelativeFilePath(image.Url, out var relativePath))
        {
            candidates.Add(Path.Combine(paths.StorageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (!string.IsNullOrWhiteSpace(image.Url) && File.Exists(image.Url))
        {
            candidates.Add(image.Url);
        }

        var sanitizedTaskId = SanitizeSegment(taskId);
        if (Directory.Exists(paths.UploadsRoot))
        {
            candidates.AddRange(Directory
                .EnumerateFiles(paths.UploadsRoot, "original.*", SearchOption.AllDirectories)
                .Where(path => path.Contains(sanitizedTaskId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var resolvedRelativePath = Path.GetRelativePath(paths.StorageRoot, candidate).Replace('\\', '/');
            var url = BuildPublicFileUrl(candidate, publicBaseUrl);
            var resolvedSha256 = await ComputeSha256Async(candidate, cancellationToken);
            var size = new FileInfo(candidate).Length;

            return new SourceImageFile(candidate, resolvedRelativePath, url, resolvedSha256, size);
        }

        return null;
    }

    public DetectionTaskWorkspace CreateTaskWorkspace(string taskId)
    {
        var taskFolder = GetTaskFolder(paths.ArchiveRoot, taskId);
        var metadataRoot = Path.Combine(taskFolder, "metadata");
        var reportsRoot = Path.Combine(taskFolder, "reports");
        var cropsRoot = Path.Combine(taskFolder, "crops");
        var terminalCropsRoot = Path.Combine(cropsRoot, "terminals");
        var wireTagCropsRoot = Path.Combine(cropsRoot, "wire-tags");
        var cacheRoot = Path.Combine(taskFolder, "cache");
        var visualsRoot = Path.Combine(taskFolder, "visuals");

        Directory.CreateDirectory(metadataRoot);
        Directory.CreateDirectory(reportsRoot);
        Directory.CreateDirectory(terminalCropsRoot);
        Directory.CreateDirectory(wireTagCropsRoot);
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(visualsRoot);

        return new DetectionTaskWorkspace(
            taskId,
            taskFolder,
            metadataRoot,
            reportsRoot,
            cropsRoot,
            terminalCropsRoot,
            wireTagCropsRoot,
            cacheRoot,
            visualsRoot);
    }

    public async Task SaveTaskRecordAsync(DetectionTaskRecord record, CancellationToken cancellationToken)
    {
        var workspace = CreateTaskWorkspace(record.TaskId);
        await SaveJsonAsync(Path.Combine(workspace.MetadataRoot, "task.json"), record, cancellationToken);
    }

    public async Task SaveJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.StorageRoot);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task SaveTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.StorageRoot);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public string BuildPublicFileUrl(string path, string publicBaseUrl)
    {
        var relativePath = Path.GetRelativePath(paths.StorageRoot, path).Replace('\\', '/');
        return $"{publicBaseUrl.TrimEnd('/')}/files/{relativePath}";
    }

    private static bool TryGetRelativeFilePath(string url, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        const string filesMarker = "/files/";
        var markerIndex = uri.AbsolutePath.IndexOf(filesMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        relativePath = Uri.UnescapeDataString(uri.AbsolutePath[(markerIndex + filesMarker.Length)..]);
        return !string.IsNullOrWhiteSpace(relativePath);
    }

    private static string GetTaskFolder(string root, string taskId)
    {
        var now = DateTimeOffset.Now;
        return Path.Combine(
            root,
            now.Year.ToString("0000"),
            now.Month.ToString("00"),
            now.Day.ToString("00"),
            SanitizeSegment(taskId));
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
