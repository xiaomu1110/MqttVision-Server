using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using MqttVision.Server.Configuration;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public sealed class JsonConfigurationImportService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Channel<JsonConfigurationImportWorkItem> queue = Channel.CreateUnbounded<JsonConfigurationImportWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<string, ConfigurationImportBatchRecord> batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerPathInitializer paths;
    private readonly RuntimeConfigurationService configuration;
    private readonly IJsonConfigurationParser parser;
    private readonly ILogger<JsonConfigurationImportService> logger;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private readonly object stateGate = new();

    public JsonConfigurationImportService(
        ServerPathInitializer paths,
        RuntimeConfigurationService configuration,
        IJsonConfigurationParser parser,
        ILogger<JsonConfigurationImportService> logger)
    {
        this.paths = paths;
        this.configuration = configuration;
        this.parser = parser;
        this.logger = logger;
        LoadPersistedBatches();
    }

    public IReadOnlyList<ConfigurationImportBatchRecord> ListBatches()
    {
        lock (stateGate)
        {
            return batches.Values
                .OrderByDescending(batch => batch.CreatedAt)
                .Select(CloneBatch)
                .ToArray();
        }
    }

    public ConfigurationImportBatchRecord? GetBatch(string batchId)
    {
        lock (stateGate)
        {
            return batches.TryGetValue(batchId, out var batch) ? CloneBatch(batch) : null;
        }
    }

    public async Task<ConfigurationImportBatchRecord> CreateBatchAsync(
        IReadOnlyList<ConfigurationImportUpload> uploads,
        CancellationToken cancellationToken = default)
    {
        if (uploads.Count == 0)
        {
            throw new InvalidOperationException("至少需要选择一个 JSON 配置文件。");
        }

        var importOptions = configuration.Current.JsonImport;
        var allowedExtensions = importOptions.AllowedExtensions
            .Select(extension => extension.Trim().ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = uploads.FirstOrDefault(upload =>
            !allowedExtensions.Contains(Path.GetExtension(upload.FileName).ToLowerInvariant()));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"文件 {invalid.FileName} 不是支持的 JSON 配置格式，仅支持 {string.Join("、", allowedExtensions)}。");
        }

        var oversized = uploads.FirstOrDefault(upload => upload.Length <= 0 || upload.Length > importOptions.MaxFileBytes);
        if (oversized is not null)
        {
            throw new InvalidOperationException($"文件 {oversized.FileName} 为空或超过 JSON 配置文件大小限制 {importOptions.MaxFileBytes} 字节。");
        }

        var now = DateTimeOffset.Now;
        var batchId = CreateBatchId(now);
        var batchRoot = Path.Combine(
            paths.ConfigurationImportsRoot,
            now.Year.ToString("0000", CultureInfo.InvariantCulture),
            now.Month.ToString("00", CultureInfo.InvariantCulture),
            now.Day.ToString("00", CultureInfo.InvariantCulture),
            batchId);
        var sourceRoot = Directory.CreateDirectory(Path.Combine(batchRoot, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(batchRoot, "backup"));
        var statePath = Path.Combine(batchRoot, "batch.json");
        var usedCabinetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ConfigurationImportFileRecord>();

        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = $"file-{Guid.NewGuid():N}";
            var originalName = Path.GetFileName(upload.FileName);
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            var cabinetId = MakeUniqueCabinetId(Path.GetFileNameWithoutExtension(originalName), usedCabinetIds);
            var sourcePath = Path.Combine(sourceRoot, $"{fileId}{extension}");
            await using (var input = await upload.OpenReadAsync(cancellationToken))
            await using (var output = File.Create(sourcePath))
            {
                await CopyWithLimitAsync(input, output, importOptions.MaxFileBytes, cancellationToken);
            }

            var timestamp = DateTimeOffset.Now;
            files.Add(new ConfigurationImportFileRecord
            {
                FileId = fileId,
                OriginalFileName = originalName,
                CabinetId = cabinetId,
                FileSize = upload.Length,
                ContentType = upload.ContentType,
                Extension = extension,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                SourcePath = sourcePath,
                SourceUrl = BuildPublicUrl(sourcePath)
            });
        }

        var batch = new ConfigurationImportBatchRecord
        {
            BatchId = batchId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = ConfigurationImportBatchStatus.Queued,
            RootPath = batchRoot,
            StatePath = statePath,
            Files = files
        };
        lock (stateGate)
        {
            batches[batchId] = batch;
        }

        await PersistAsync(batch, cancellationToken);
        foreach (var file in files)
        {
            await queue.Writer.WriteAsync(new JsonConfigurationImportWorkItem(batchId, file.FileId), cancellationToken);
        }

        lock (stateGate)
        {
            return CloneBatch(batch);
        }
    }

    internal static string CreateBatchId(DateTimeOffset now)
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        return $"json-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{token}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Clamp(configuration.Current.JsonImport.MaxConcurrentImports, 1, 3);
        var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var workItem in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessFileAsync(workItem, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "JSON configuration import worker failed. BatchId={BatchId}, FileId={FileId}", workItem.BatchId, workItem.FileId);
                try
                {
                    await MarkFileFailedAsync(workItem, ex.Message, CancellationToken.None);
                }
                catch (Exception persistException)
                {
                    logger.LogError(
                        persistException,
                        "Unable to persist failed JSON configuration import state. BatchId={BatchId}, FileId={FileId}",
                        workItem.BatchId,
                        workItem.FileId);
                }
            }
        }
    }

    private async Task ProcessFileAsync(JsonConfigurationImportWorkItem workItem, CancellationToken cancellationToken)
    {
        if (!batches.TryGetValue(workItem.BatchId, out var batch))
        {
            return;
        }

        var file = batch.Files.FirstOrDefault(item => string.Equals(item.FileId, workItem.FileId, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return;
        }

        UpdateFile(batch, file, ConfigurationImportFileStatus.Processing, 10, null);
        await PersistAsync(batch, cancellationToken);
        var configRoot = paths.ResolvePath(configuration.Current.Processing.CabinetConfigurationRoot);
        Directory.CreateDirectory(configRoot);
        var configPath = Path.Combine(configRoot, $"{file.CabinetId}.json");
        var backupPath = Path.Combine(batch.RootPath, "backup", $"{file.CabinetId}.json");

        try
        {
            var sha256 = await ComputeSha256Async(file.SourcePath, cancellationToken);
            var source = new JsonConfigurationSource
            {
                OriginalFileName = file.OriginalFileName,
                Sha256 = sha256,
                Format = "json-import-v1",
                SourcePath = file.SourcePath,
                ImportedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
            };
            UpdateFile(batch, file, ConfigurationImportFileStatus.Processing, 25, null);
            await PersistAsync(batch, cancellationToken);

            var parsed = await parser.ParseAsync(file.SourcePath, file.CabinetId, source, cancellationToken);
            if (parsed.Configuration.TerminalStrips.Count == 0 || parsed.Configuration.Terminals.Count == 0)
            {
                throw new InvalidOperationException("JSON 中没有可用的端子排或端子关系。");
            }

            UpdateFile(batch, file, ConfigurationImportFileStatus.Processing, 65, null);
            await PersistAsync(batch, cancellationToken);

            var previousConfigPath = File.Exists(configPath) ? configPath : null;
            if (previousConfigPath is not null)
            {
                File.Copy(previousConfigPath, backupPath, true);
            }
            else
            {
                await File.WriteAllTextAsync(backupPath, "{\n  \"message\": \"导入前不存在同名柜体配置\"\n}\n", cancellationToken);
            }

            var temporaryPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(parsed.Configuration, JsonOptions), cancellationToken);
            try
            {
                File.Move(temporaryPath, configPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            lock (stateGate)
            {
                file.PreviousConfigPath = previousConfigPath;
                file.ConfigPath = configPath;
                file.BackupPath = backupPath;
                file.ConfigUrl = BuildPublicUrl(configPath);
                file.BackupUrl = BuildPublicUrl(backupPath);
                file.Sha256 = sha256;
                file.TerminalStripCount = parsed.Configuration.TerminalStrips.Count;
                file.TerminalCount = parsed.Configuration.Terminals.Count;
                file.Warnings.AddRange(parsed.Warnings);
                file.PreviewRows.AddRange(parsed.Configuration.TerminalStrips
                    .SelectMany(strip => strip.Terminals.Select(terminal => new ConfigurationImportTerminalPreview
                    {
                        StripCode = strip.StripCode,
                        TerminalLabel = terminal.TerminalLabel ?? terminal.TerminalNumber.ToString(CultureInfo.InvariantCulture),
                        WireMarkers = (terminal.WireMarkers ?? []).ToList(),
                        IsExpectedEmpty = terminal.IsExpectedEmpty
                    })));
            }
            UpdateFile(batch, file, ConfigurationImportFileStatus.Completed, 100, null);
            await PersistAsync(batch, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFileFailedAsync(workItem, ex.Message, CancellationToken.None);
        }
    }

    private async Task MarkFileFailedAsync(
        JsonConfigurationImportWorkItem workItem,
        string message,
        CancellationToken cancellationToken)
    {
        if (!batches.TryGetValue(workItem.BatchId, out var batch))
        {
            return;
        }

        var file = batch.Files.FirstOrDefault(item => string.Equals(item.FileId, workItem.FileId, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return;
        }

        UpdateFile(batch, file, ConfigurationImportFileStatus.Failed, file.ProgressPercent, message);
        await PersistAsync(batch, cancellationToken);
    }

    private void UpdateFile(
        ConfigurationImportBatchRecord batch,
        ConfigurationImportFileRecord file,
        ConfigurationImportFileStatus status,
        int progress,
        string? error)
    {
        lock (stateGate)
        {
            file.Status = status;
            file.ProgressPercent = Math.Clamp(progress, 0, 100);
            file.ErrorMessage = error;
            file.UpdatedAt = DateTimeOffset.Now;
            batch.UpdatedAt = DateTimeOffset.Now;
            batch.Status = batch.Files.Any(item => item.Status == ConfigurationImportFileStatus.Processing)
                ? ConfigurationImportBatchStatus.Processing
                : batch.Files.All(item => item.Status == ConfigurationImportFileStatus.Completed)
                    ? ConfigurationImportBatchStatus.Completed
                    : batch.Files.All(item => item.Status is ConfigurationImportFileStatus.Completed or ConfigurationImportFileStatus.Failed)
                        ? (batch.Files.Any(item => item.Status == ConfigurationImportFileStatus.Completed)
                            ? ConfigurationImportBatchStatus.CompletedWithErrors
                            : ConfigurationImportBatchStatus.Failed)
                        : ConfigurationImportBatchStatus.Queued;
        }
    }

    private async Task PersistAsync(ConfigurationImportBatchRecord batch, CancellationToken cancellationToken)
    {
        await stateLock.WaitAsync(cancellationToken);
        try
        {
            string snapshot;
            lock (stateGate)
            {
                snapshot = JsonSerializer.Serialize(batch, JsonOptions);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(batch.StatePath)!);
            var temporaryPath = $"{batch.StatePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, snapshot, cancellationToken);
            File.Move(temporaryPath, batch.StatePath, true);
        }
        finally
        {
            stateLock.Release();
        }
    }

    private void LoadPersistedBatches()
    {
        if (!Directory.Exists(paths.ConfigurationImportsRoot))
        {
            return;
        }

        foreach (var statePath in Directory.EnumerateFiles(paths.ConfigurationImportsRoot, "batch.json", SearchOption.AllDirectories))
        {
            try
            {
                var batch = JsonSerializer.Deserialize<ConfigurationImportBatchRecord>(File.ReadAllText(statePath), JsonOptions);
                if (batch is null || string.IsNullOrWhiteSpace(batch.BatchId))
                {
                    continue;
                }

                foreach (var file in batch.Files.Where(file => file.Status is ConfigurationImportFileStatus.Queued or ConfigurationImportFileStatus.Processing))
                {
                    file.Status = ConfigurationImportFileStatus.Queued;
                    file.ProgressPercent = 0;
                }

                batches[batch.BatchId] = batch;
                foreach (var file in batch.Files.Where(file => file.Status == ConfigurationImportFileStatus.Queued))
                {
                    queue.Writer.TryWrite(new JsonConfigurationImportWorkItem(batch.BatchId, file.FileId));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Unable to restore JSON configuration import state. StatePath={StatePath}", statePath);
            }
        }
    }

    private string? BuildPublicUrl(string path)
    {
        var relative = Path.GetRelativePath(paths.StorageRoot, path).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        return $"{configuration.Current.PublicBaseUrl.TrimEnd('/')}/files/{relative}";
    }

    private static string MakeUniqueCabinetId(string? baseName, ISet<string> used)
    {
        var safe = new string((baseName ?? "cabinet").Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "cabinet";
        }

        var candidate = safe;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{safe}-{suffix++}";
        }

        return candidate;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            copied += read;
            if (copied > maximumBytes)
            {
                throw new InvalidOperationException($"JSON 配置文件实际大小超过限制 {maximumBytes} 字节。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static ConfigurationImportBatchRecord CloneBatch(ConfigurationImportBatchRecord batch) =>
        JsonSerializer.Deserialize<ConfigurationImportBatchRecord>(JsonSerializer.Serialize(batch, JsonOptions), JsonOptions) ?? new();

    private sealed record JsonConfigurationImportWorkItem(string BatchId, string FileId);
}
