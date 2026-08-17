using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MqttVision.Server.Configuration;
using MqttVision.Server.Domain;
using MqttVision.Server.Infrastructure.Cad;

namespace MqttVision.Server.Application;

public sealed class CadImportService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Channel<CadImportWorkItem> queue = Channel.CreateUnbounded<CadImportWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<string, CadImportBatchRecord> batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerPathInitializer paths;
    private readonly RuntimeConfigurationService configuration;
    private readonly ICadConfigurationParser parser;
    private readonly ILogger<CadImportService> logger;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private readonly object stateGate = new();

    public CadImportService(
        ServerPathInitializer paths,
        RuntimeConfigurationService configuration,
        ICadConfigurationParser parser,
        ILogger<CadImportService> logger)
    {
        this.paths = paths;
        this.configuration = configuration;
        this.parser = parser;
        this.logger = logger;
        LoadPersistedBatches();
    }

    public IReadOnlyList<CadImportBatchRecord> ListBatches()
    {
        lock (stateGate)
        {
            return batches.Values
                .OrderByDescending(batch => batch.CreatedAt)
                .Select(CloneBatch)
                .ToArray();
        }
    }

    public CadImportBatchRecord? GetBatch(string batchId)
    {
        lock (stateGate)
        {
            return batches.TryGetValue(batchId, out var batch) ? CloneBatch(batch) : null;
        }
    }

    public async Task<CadImportBatchRecord> CreateBatchAsync(
        IReadOnlyList<CadImportUpload> uploads,
        CancellationToken cancellationToken = default)
    {
        if (uploads.Count == 0)
        {
            throw new InvalidOperationException("至少需要选择一个 DWG 或 DXF 文件。");
        }

        var cadOptions = configuration.Current.CadImport;
        var allowedExtensions = cadOptions.AllowedExtensions
            .Select(extension => extension.Trim().ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = uploads.FirstOrDefault(upload =>
            !allowedExtensions.Contains(Path.GetExtension(upload.FileName).ToLowerInvariant()));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"文件 {invalid.FileName} 不是支持的 CAD 格式，仅支持 {string.Join("、", allowedExtensions)}。");
        }

        var oversized = uploads.FirstOrDefault(upload => upload.Length <= 0 || upload.Length > cadOptions.MaxFileBytes);
        if (oversized is not null)
        {
            throw new InvalidOperationException($"文件 {oversized.FileName} 为空或超过 CAD 文件大小限制 {cadOptions.MaxFileBytes} 字节。");
        }

        var now = DateTimeOffset.Now;
        var batchId = CreateBatchId(now);
        var batchRoot = Path.Combine(
            paths.CadImportsRoot,
            now.Year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            now.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            now.Day.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            batchId);
        Directory.CreateDirectory(batchRoot);
        var sourceRoot = Directory.CreateDirectory(Path.Combine(batchRoot, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(batchRoot, "parsed"));
        Directory.CreateDirectory(Path.Combine(batchRoot, "backup"));
        var statePath = Path.Combine(batchRoot, "batch.json");
        var usedCabinetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<CadImportFileRecord>();

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
                await CopyWithLimitAsync(input, output, cadOptions.MaxFileBytes, cancellationToken);
            }

            var timestamp = DateTimeOffset.Now;
            files.Add(new CadImportFileRecord
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

        var batch = new CadImportBatchRecord
        {
            BatchId = batchId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = CadImportBatchStatus.Queued,
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
            await queue.Writer.WriteAsync(new CadImportWorkItem(batchId, file.FileId), cancellationToken);
        }

        lock (stateGate)
        {
            return CloneBatch(batch);
        }
    }

    internal static string CreateBatchId(DateTimeOffset now)
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        return $"cad-{now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{token}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Clamp(configuration.Current.CadImport.MaxConcurrentParsers, 1, 3);
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
                logger.LogError(ex, "CAD import worker failed. BatchId={BatchId}, FileId={FileId}", workItem.BatchId, workItem.FileId);
                MarkFileFailed(workItem, ex.Message);
            }
        }
    }

    private async Task ProcessFileAsync(CadImportWorkItem workItem, CancellationToken cancellationToken)
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

        UpdateFile(batch, file, CadImportFileStatus.Processing, 10, null);
        await PersistAsync(batch, cancellationToken);
        var root = batch.RootPath;
        var rawPath = Path.Combine(root, "parsed", $"{file.FileId}_extracted_text.json");
        var relationPath = Path.Combine(root, "parsed", $"{file.FileId}_relations.json");
        var configRoot = configuration.Current.Processing.CabinetConfigurationRoot;
        var resolvedConfigRoot = paths.ResolvePath(configRoot);
        Directory.CreateDirectory(resolvedConfigRoot);
        var configPath = Path.Combine(resolvedConfigRoot, $"{file.CabinetId}.json");
        var backupPath = Path.Combine(root, "backup", $"{file.CabinetId}.json");

        try
        {
            var sha256 = await ComputeSha256Async(file.SourcePath, cancellationToken);
            var source = new CadConfigurationSource
            {
                OriginalFileName = file.OriginalFileName,
                Sha256 = sha256,
                ParserProfile = "cad-terminal-table-v1",
                ImportedAt = DateTimeOffset.Now.ToString("O"),
                RawTextPath = rawPath,
                RelationPath = relationPath
            };
            UpdateFile(batch, file, CadImportFileStatus.Processing, 25, null);
            await PersistAsync(batch, cancellationToken);

            var timeout = TimeSpan.FromSeconds(Math.Max(10, configuration.Current.CadImport.ParserTimeoutSeconds));
            var parseTask = Task.Run(() => parser.Parse(file.SourcePath, file.CabinetId, source), cancellationToken);
            var parsed = await parseTask.WaitAsync(timeout, cancellationToken);
            if (parsed.Configuration.TerminalStrips.Count == 0 || parsed.Configuration.Terminals.Count == 0)
            {
                throw new InvalidOperationException("未识别到包含端子号的端子排列表，请检查 CAD 图纸文本或解析格式。");
            }

            var jsonOptions = new JsonSerializerOptions(JsonOptions)
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            await File.WriteAllTextAsync(rawPath, JsonSerializer.Serialize(parsed.ExtractedText, jsonOptions), cancellationToken);
            await File.WriteAllTextAsync(
                relationPath,
                JsonSerializer.Serialize(
                    new
                    {
                        source,
                        warnings = parsed.Warnings,
                        strips = parsed.Configuration.TerminalStrips
                    },
                    jsonOptions),
                cancellationToken);
            UpdateFile(batch, file, CadImportFileStatus.Processing, 65, null);
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

            var tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(parsed.Configuration, jsonOptions), cancellationToken);
            try
            {
                File.Move(tempPath, configPath, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            lock (stateGate)
            {
                file.PreviousConfigPath = previousConfigPath;
                file.RawTextPath = rawPath;
                file.RelationsPath = relationPath;
                file.ConfigPath = configPath;
                file.BackupPath = backupPath;
                file.RawTextUrl = BuildPublicUrl(rawPath);
                file.RelationsUrl = BuildPublicUrl(relationPath);
                file.ConfigUrl = BuildPublicUrl(configPath);
                file.BackupUrl = BuildPublicUrl(backupPath);
                file.Sha256 = sha256;
                file.ExtractedTextCount = parsed.ExtractedText.Count;
                file.TerminalStripCount = parsed.Configuration.TerminalStrips.Count;
                file.TerminalCount = parsed.Configuration.Terminals.Count;
                file.Warnings.AddRange(parsed.Warnings);
                file.PreviewRows.AddRange(parsed.Configuration.TerminalStrips
                    .SelectMany(strip => strip.Terminals.Select(terminal => new CadImportTerminalPreview
                    {
                        StripCode = strip.StripCode,
                        Orientation = strip.Orientation,
                        TerminalLabel = terminal.TerminalLabel ?? terminal.TerminalNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        LeftWireMarker = terminal.LeftWireMarker,
                        RightWireMarker = terminal.RightWireMarker,
                        AuxiliaryValue = terminal.AuxiliaryValue,
                        Destination = terminal.Destination,
                        IsExpectedEmpty = terminal.IsExpectedEmpty
                    })));
            }
            UpdateFile(batch, file, CadImportFileStatus.Completed, 100, null);
            await PersistAsync(batch, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkFileFailed(workItem, ex.Message);
            await PersistAsync(batch, CancellationToken.None);
        }
    }

    private void MarkFileFailed(CadImportWorkItem workItem, string message)
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

        UpdateFile(batch, file, CadImportFileStatus.Failed, file.ProgressPercent, message);
        _ = PersistAsync(batch, CancellationToken.None);
    }

    private void UpdateFile(
        CadImportBatchRecord batch,
        CadImportFileRecord file,
        CadImportFileStatus status,
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
            batch.Status = batch.Files.Any(item => item.Status == CadImportFileStatus.Processing)
                ? CadImportBatchStatus.Processing
                : batch.Files.All(item => item.Status == CadImportFileStatus.Completed)
                    ? CadImportBatchStatus.Completed
                    : batch.Files.All(item => item.Status is CadImportFileStatus.Completed or CadImportFileStatus.Failed)
                        ? (batch.Files.Any(item => item.Status == CadImportFileStatus.Completed)
                            ? CadImportBatchStatus.CompletedWithErrors
                            : CadImportBatchStatus.Failed)
                        : CadImportBatchStatus.Queued;
        }
    }

    private async Task PersistAsync(CadImportBatchRecord batch, CancellationToken cancellationToken)
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
        if (!Directory.Exists(paths.CadImportsRoot))
        {
            return;
        }

        foreach (var statePath in Directory.EnumerateFiles(paths.CadImportsRoot, "batch.json", SearchOption.AllDirectories))
        {
            try
            {
                var batch = JsonSerializer.Deserialize<CadImportBatchRecord>(File.ReadAllText(statePath), JsonOptions);
                if (batch is null || string.IsNullOrWhiteSpace(batch.BatchId))
                {
                    continue;
                }

                foreach (var file in batch.Files.Where(file => file.Status is CadImportFileStatus.Queued or CadImportFileStatus.Processing))
                {
                    file.Status = CadImportFileStatus.Queued;
                    file.ProgressPercent = 0;
                }

                batches[batch.BatchId] = batch;
                foreach (var file in batch.Files.Where(file => file.Status == CadImportFileStatus.Queued))
                {
                    queue.Writer.TryWrite(new CadImportWorkItem(batch.BatchId, file.FileId));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Unable to restore CAD import state. StatePath={StatePath}", statePath);
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
                throw new InvalidOperationException($"CAD 文件实际大小超过限制 {maximumBytes} 字节。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static CadImportBatchRecord CloneBatch(CadImportBatchRecord batch) =>
        JsonSerializer.Deserialize<CadImportBatchRecord>(JsonSerializer.Serialize(batch, JsonOptions), JsonOptions) ?? new();

    private sealed record CadImportWorkItem(string BatchId, string FileId);
}
