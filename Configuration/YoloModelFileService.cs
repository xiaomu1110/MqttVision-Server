namespace MqttVision.Server.Configuration;

public sealed record YoloModelFileDescriptor(
    string FileName,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedAt,
    bool IsBuiltIn,
    bool IsSelected);

public sealed class YoloModelFileService : IDisposable
{
    public const long MaxUploadBytes = 512L * 1024 * 1024;

    private readonly IHostEnvironment environment;
    private readonly ServerPathInitializer paths;
    private readonly SemaphoreSlim uploadLock = new(1, 1);

    public YoloModelFileService(
        IHostEnvironment environment,
        ServerPathInitializer paths)
    {
        this.environment = environment;
        this.paths = paths;
    }

    public string UploadedModelsRoot => Path.Combine(paths.StorageRoot, "models");

    public IReadOnlyList<YoloModelFileDescriptor> List(string? selectedModelPath = null)
    {
        var selectedPath = ResolveConfiguredPath(selectedModelPath);
        var descriptors = new Dictionary<string, YoloModelFileDescriptor>(StringComparer.OrdinalIgnoreCase);

        AddDirectoryModels(
            Path.Combine(environment.ContentRootPath, "Models"),
            isBuiltIn: true,
            selectedPath,
            descriptors);
        AddDirectoryModels(
            UploadedModelsRoot,
            isBuiltIn: false,
            selectedPath,
            descriptors);

        if (selectedPath is not null && File.Exists(selectedPath) && !descriptors.ContainsKey(selectedPath))
        {
            descriptors[selectedPath] = Describe(selectedPath, isBuiltIn: false, selectedPath);
        }

        return descriptors.Values
            .OrderByDescending(model => model.IsSelected)
            .ThenBy(model => model.IsBuiltIn ? 0 : 1)
            .ThenBy(model => model.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<YoloModelFileDescriptor> UploadAsync(
        string fileName,
        long length,
        Func<CancellationToken, Task<Stream>> openRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openRead);

        if (length <= 0)
        {
            throw new InvalidOperationException("模型文件为空。");
        }

        if (length > MaxUploadBytes)
        {
            throw new InvalidOperationException($"模型文件超过限制：{FormatBytes(MaxUploadBytes)}。");
        }

        var safeFileName = NormalizeFileName(fileName);
        await uploadLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(UploadedModelsRoot);
            var destination = GetAvailablePath(safeFileName);
            var temporaryPath = $"{destination}.{Guid.NewGuid():N}.uploading";

            try
            {
                await using (var source = await openRead(cancellationToken))
                await using (var target = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                }

                var actualLength = new FileInfo(temporaryPath).Length;
                if (actualLength <= 0)
                {
                    throw new InvalidOperationException("模型文件为空。");
                }

                if (actualLength > MaxUploadBytes)
                {
                    throw new InvalidOperationException($"模型文件超过限制：{FormatBytes(MaxUploadBytes)}。");
                }

                File.Move(temporaryPath, destination);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return Describe(destination, isBuiltIn: false, selectedPath: null);
        }
        finally
        {
            uploadLock.Release();
        }
    }

    public void Dispose() => uploadLock.Dispose();

    private void AddDirectoryModels(
        string root,
        bool isBuiltIn,
        string? selectedPath,
        IDictionary<string, YoloModelFileDescriptor> descriptors)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            descriptors[fullPath] = Describe(fullPath, isBuiltIn, selectedPath);
        }
    }

    private YoloModelFileDescriptor Describe(string path, bool isBuiltIn, string? selectedPath)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(environment.ContentRootPath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal))
        {
            relativePath = fullPath;
        }
        var normalizedSelectedPath = selectedPath is null ? null : Path.GetFullPath(selectedPath);

        return new YoloModelFileDescriptor(
            Path.GetFileName(fullPath),
            relativePath,
            new FileInfo(fullPath).Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)),
            isBuiltIn,
            normalizedSelectedPath is not null &&
                string.Equals(fullPath, normalizedSelectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveConfiguredPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(modelPath.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(environment.ContentRootPath, expanded));
    }

    private string GetAvailablePath(string fileName)
    {
        var destination = Path.Combine(UploadedModelsRoot, fileName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(UploadedModelsRoot, $"{stem}-{suffix}{extension}");
    }

    private static string NormalizeFileName(string fileName)
    {
        var normalizedInput = fileName?.Trim().Replace('\\', '/');
        var originalName = Path.GetFileName(normalizedInput);
        if (string.IsNullOrWhiteSpace(originalName) ||
            !string.Equals(Path.GetExtension(originalName), ".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只支持上传 .onnx 目标检测模型文件。");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var normalizedStem = new string(stem
            .Select(character => invalidChars.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray())
            .Trim()
            .Trim('.');
        if (string.IsNullOrWhiteSpace(normalizedStem))
        {
            normalizedStem = "model";
        }

        if (normalizedStem.Length > 120)
        {
            normalizedStem = normalizedStem[..120];
        }

        return $"{normalizedStem}.onnx";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
