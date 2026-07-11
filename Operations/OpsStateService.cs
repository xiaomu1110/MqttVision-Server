using System.Collections.Concurrent;
using System.Text.Json;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Operations;

public sealed class OpsStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, OpsLiveTask> liveTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OpsComponentState> components = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<OpsAlertItem> alerts = new();
    private readonly RuntimeConfigurationService configuration;
    private readonly ServerPathInitializer paths;

    public OpsStateService(
        RuntimeConfigurationService configuration,
        ServerPathInitializer paths)
    {
        this.configuration = configuration;
        this.paths = paths;
        var options = this.configuration.Current;
        components["mqtt-subscriber"] = OpsComponentState.Unknown("MQTT Subscriber");
        components["mqtt-publisher"] = OpsComponentState.Unknown("MQTT Publisher");
        components["ocr"] = new OpsComponentState
        {
            Name = "PaddleOCR Serving",
            Status = options.Processing.PaddleOcrEnabled ? "configured" : "disabled",
            Message = options.Processing.PaddleOcrEnabled
                ? options.Processing.PaddleOcrServiceUrl
                : "PaddleOCR 未启用。"
        };
        components["yolo"] = BuildYoloModelState();
    }

    public void RecordMqttState(string role, string status, string message)
    {
        components[$"mqtt-{role}"] = new OpsComponentState
        {
            Name = role.Equals("publisher", StringComparison.OrdinalIgnoreCase)
                ? "MQTT Publisher"
                : "MQTT Subscriber",
            Status = status,
            Message = message,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    public void RecordOcrState(string status, string message)
    {
        components["ocr"] = new OpsComponentState
        {
            Name = "PaddleOCR Serving",
            Status = status,
            Message = message,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    public void RecordTaskStage(DetectionTaskRecord record, string stage, string message)
    {
        var task = liveTasks.GetOrAdd(record.TaskId, _ => OpsLiveTask.From(record));
        task.Update(record, stage, message);
    }

    public void RecordTaskResult(DetectionTaskRecord record, DetectionPipelineResult result)
    {
        var task = liveTasks.GetOrAdd(record.TaskId, _ => OpsLiveTask.From(record));
        task.Update(record, result.Status, result.Message);
        task.ResultJsonUrl = result.ResultJsonUrl;
        task.ReportUrl = result.ReportUrl;
        task.VisualSummaryUrl = result.VisualSummaryUrl;
        task.TerminalCount = result.Summary.TerminalCount;
        task.WireTagCount = result.Summary.WireTagCount;
        task.MatchedCount = result.Summary.ConfigurationMatchedCount;
        task.MismatchCount = result.Summary.ConfigurationMismatchCount;
        task.UnrecognizedCount = result.Summary.ConfigurationUnrecognizedCount;

        if (!result.Success)
        {
            AddAlert("error", "检测任务失败", result.ErrorMessage ?? result.Message, record.TaskId);
            return;
        }

        if (result.Summary.ConfigurationMismatchCount > 0)
        {
            AddAlert(
                "warning",
                "发现疑似错接",
                $"任务 {record.TaskId} 有 {result.Summary.ConfigurationMismatchCount} 项需要复查。",
                record.TaskId);
        }

        if (result.Summary.ConfigurationUnrecognizedCount > 0)
        {
            AddAlert(
                "warning",
                "存在无法识别项",
                $"任务 {record.TaskId} 有 {result.Summary.ConfigurationUnrecognizedCount} 项无法识别。",
                record.TaskId);
        }
    }

    public void RecordTaskFailure(DetectionTaskRecord record, string message)
    {
        var task = liveTasks.GetOrAdd(record.TaskId, _ => OpsLiveTask.From(record));
        task.Update(record, "failed", message);
        AddAlert("error", "检测任务失败", message, record.TaskId);
    }

    public async Task<OpsDashboardSnapshot> GetSnapshotAsync(
        string publicBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var options = configuration.Current;
        components["yolo"] = BuildYoloModelState();

        var archiveTasks = await ReadArchiveTasksAsync(publicBaseUrl, cancellationToken);
        var taskById = archiveTasks
            .GroupBy(task => task.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(task => task.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var liveTask in liveTasks.Values.Select(task => task.ToRow(publicBaseUrl)))
        {
            taskById[liveTask.TaskId] = liveTask;
        }

        var tasks = taskById.Values
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();
        var today = DateTimeOffset.Now.Date;
        var todayTasks = tasks
            .Where(task => task.CreatedAt.Date == today || task.UpdatedAt.Date == today)
            .ToArray();

        return new OpsDashboardSnapshot
        {
            ServerTime = DateTimeOffset.Now,
            Counters = new OpsCounters
            {
                TodayTasks = todayTasks.Length,
                CompletedTasks = todayTasks.Count(task => IsCompleted(task.Status)),
                FailedTasks = todayTasks.Count(task => IsFailed(task.Status)),
                ActiveTasks = liveTasks.Values.Count(task => IsActive(task.Status)),
                QueuedTasks = liveTasks.Values.Count(task => task.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)),
                MatchedCount = todayTasks.Sum(task => task.MatchedCount),
                MismatchCount = todayTasks.Sum(task => task.MismatchCount),
                UnrecognizedCount = todayTasks.Sum(task => task.UnrecognizedCount)
            },
            MqttSubscriber = components.GetValueOrDefault("mqtt-subscriber") ?? OpsComponentState.Unknown("MQTT Subscriber"),
            MqttPublisher = components.GetValueOrDefault("mqtt-publisher") ?? OpsComponentState.Unknown("MQTT Publisher"),
            OcrService = components.GetValueOrDefault("ocr") ?? OpsComponentState.Unknown("PaddleOCR Serving"),
            YoloModel = components.GetValueOrDefault("yolo") ?? BuildYoloModelState(),
            StorageRoot = paths.StorageRoot,
            ArchiveSizeText = FormatBytes(GetDirectorySize(paths.ArchiveRoot)),
            YoloModelPath = options.Processing.YoloOnnxModelPath,
            OcrServiceUrl = options.Processing.PaddleOcrServiceUrl,
            RecentTasks = tasks.Take(24).ToArray(),
            Alerts = BuildAlerts(tasks).Take(40).ToArray()
        };
    }

    private async Task<IReadOnlyList<OpsTaskRow>> ReadArchiveTasksAsync(
        string publicBaseUrl,
        CancellationToken cancellationToken)
    {
        var archiveRoot = paths.ArchiveRoot;
        if (!Directory.Exists(archiveRoot))
        {
            return Array.Empty<OpsTaskRow>();
        }

        var resultFiles = Directory
            .EnumerateFiles(archiveRoot, "detection-result.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(200)
            .ToArray();
        var tasks = new List<OpsTaskRow>(resultFiles.Length);

        foreach (var resultFile in resultFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(resultFile);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                var taskId = ReadString(root, "taskId");
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    continue;
                }

                var metadata = await ReadTaskMetadataAsync(resultFile, cancellationToken);
                var summary = TryGetObject(root, "summary", out var summaryObject)
                    ? summaryObject
                    : default;
                var artifacts = TryGetObject(root, "artifacts", out var artifactsObject)
                    ? artifactsObject
                    : default;
                var generatedAt = ReadDate(root, "generatedAt") ?? File.GetLastWriteTime(resultFile);

                tasks.Add(new OpsTaskRow
                {
                    TaskId = taskId,
                    DeviceId = metadata.DeviceId,
                    SiteId = metadata.SiteId,
                    CabinetId = metadata.CabinetId,
                    Stage = ReadString(root, "status") ?? "completed",
                    Status = metadata.Status.Length > 0 ? metadata.Status : ReadString(root, "status") ?? "completed",
                    Message = "检测归档已生成。",
                    CreatedAt = metadata.CreatedAt ?? generatedAt,
                    UpdatedAt = generatedAt,
                    TerminalCount = ReadInt(summary, "terminalCount"),
                    WireTagCount = ReadInt(summary, "wireTagCount"),
                    MatchedCount = ReadInt(summary, "configurationMatchedCount"),
                    MismatchCount = ReadInt(summary, "configurationMismatchCount"),
                    UnrecognizedCount = ReadInt(summary, "configurationUnrecognizedCount"),
                    ResultJsonUrl = RewriteFileUrl(ReadString(artifacts, "resultJsonUrl"), publicBaseUrl),
                    ReportUrl = RewriteFileUrl(ReadString(artifacts, "reportUrl"), publicBaseUrl),
                    VisualSummaryUrl = RewriteFileUrl(ReadString(artifacts, "visualSummaryUrl"), publicBaseUrl)
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AddAlert("warning", "归档读取失败", $"{Path.GetFileName(resultFile)} 读取失败: {ex.Message}", null);
            }
        }

        return tasks;
    }

    private async Task<TaskMetadata> ReadTaskMetadataAsync(
        string resultFile,
        CancellationToken cancellationToken)
    {
        var reportsRoot = Path.GetDirectoryName(resultFile);
        var taskRoot = reportsRoot is null ? null : Directory.GetParent(reportsRoot)?.FullName;
        var metadataPath = taskRoot is null ? null : Path.Combine(taskRoot, "metadata", "task.json");
        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
        {
            return new TaskMetadata();
        }

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            return new TaskMetadata
            {
                DeviceId = ReadString(root, "deviceId") ?? string.Empty,
                SiteId = ReadString(root, "siteId") ?? string.Empty,
                CabinetId = ReadString(root, "cabinetId") ?? string.Empty,
                Status = ReadString(root, "status") ?? string.Empty,
                CreatedAt = ReadDate(root, "createdAt")
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AddAlert("warning", "任务元数据读取失败", $"{Path.GetFileName(metadataPath)} 读取失败: {ex.Message}", null);
            return new TaskMetadata();
        }
    }

    private IReadOnlyList<OpsAlertItem> BuildAlerts(IReadOnlyList<OpsTaskRow> tasks)
    {
        var generatedAlerts = tasks
            .Where(task => task.UpdatedAt.Date == DateTimeOffset.Now.Date)
            .SelectMany(task =>
            {
                var items = new List<OpsAlertItem>();
                if (IsFailed(task.Status))
                {
                    items.Add(new OpsAlertItem
                    {
                        Level = "error",
                        Title = "任务失败",
                        Message = task.Message,
                        TaskId = task.TaskId,
                        CreatedAt = task.UpdatedAt
                    });
                }

                if (task.MismatchCount > 0)
                {
                    items.Add(new OpsAlertItem
                    {
                        Level = "warning",
                        Title = "疑似错接",
                        Message = $"任务 {task.TaskId} 有 {task.MismatchCount} 项需要复查。",
                        TaskId = task.TaskId,
                        CreatedAt = task.UpdatedAt
                    });
                }

                if (task.UnrecognizedCount > 0)
                {
                    items.Add(new OpsAlertItem
                    {
                        Level = "warning",
                        Title = "无法识别",
                        Message = $"任务 {task.TaskId} 有 {task.UnrecognizedCount} 项无法识别。",
                        TaskId = task.TaskId,
                        CreatedAt = task.UpdatedAt
                    });
                }

                return items;
            });

        return alerts
            .Concat(generatedAlerts)
            .GroupBy(alert => $"{alert.Level}:{alert.Title}:{alert.TaskId}:{alert.Message}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(alert => alert.CreatedAt).First())
            .OrderByDescending(alert => alert.CreatedAt)
            .ToArray();
    }

    private void AddAlert(string level, string title, string message, string? taskId)
    {
        alerts.Enqueue(new OpsAlertItem
        {
            Level = level,
            Title = title,
            Message = message,
            TaskId = taskId,
            CreatedAt = DateTimeOffset.Now
        });

        while (alerts.Count > 200 && alerts.TryDequeue(out _))
        {
        }
    }

    private OpsComponentState BuildYoloModelState()
    {
        var options = configuration.Current;
        var modelPath = options.Processing.YoloOnnxModelPath;
        var exists = !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);
        return new OpsComponentState
        {
            Name = "YOLO ONNX",
            Status = exists ? "online" : "error",
            Message = exists ? modelPath : "YOLO ONNX 模型文件不存在或未配置。",
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static string? RewriteFileUrl(string? value, string publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        const string filesMarker = "/files/";
        var markerIndex = uri.PathAndQuery.IndexOf(filesMarker, StringComparison.OrdinalIgnoreCase);
        return markerIndex < 0
            ? value
            : $"{publicBaseUrl.TrimEnd('/')}{uri.PathAndQuery[markerIndex..]}";
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int ReadInt(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => 0
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(property.GetString(), out var value)
            ? value
            : null;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory
                .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static bool IsCompleted(string status) =>
        status.Contains("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(string status) =>
        status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(string status) =>
        status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Processing", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("processing", StringComparison.OrdinalIgnoreCase);

    private sealed class TaskMetadata
    {
        public string DeviceId { get; init; } = string.Empty;

        public string SiteId { get; init; } = string.Empty;

        public string CabinetId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTimeOffset? CreatedAt { get; init; }
    }

    private sealed class OpsLiveTask
    {
        private readonly object gate = new();

        public string TaskId { get; private init; } = string.Empty;

        public string DeviceId { get; private set; } = string.Empty;

        public string SiteId { get; private set; } = string.Empty;

        public string CabinetId { get; private set; } = string.Empty;

        public string Stage { get; private set; } = string.Empty;

        public string Status { get; private set; } = string.Empty;

        public string Message { get; private set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.Now;

        public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.Now;

        public int TerminalCount { get; set; }

        public int WireTagCount { get; set; }

        public int MatchedCount { get; set; }

        public int MismatchCount { get; set; }

        public int UnrecognizedCount { get; set; }

        public string? ResultJsonUrl { get; set; }

        public string? ReportUrl { get; set; }

        public string? VisualSummaryUrl { get; set; }

        public static OpsLiveTask From(DetectionTaskRecord record) =>
            new()
            {
                TaskId = record.TaskId,
                DeviceId = record.DeviceId,
                SiteId = record.SiteId,
                CabinetId = record.CabinetId,
                Stage = record.Status.ToString(),
                Status = record.Status.ToString(),
                CreatedAt = record.CreatedAt == default ? DateTimeOffset.Now : record.CreatedAt,
                UpdatedAt = record.UpdatedAt
            };

        public void Update(DetectionTaskRecord record, string stage, string message)
        {
            lock (gate)
            {
                DeviceId = record.DeviceId;
                SiteId = record.SiteId;
                CabinetId = record.CabinetId;
                Stage = stage;
                Status = record.Status.ToString();
                Message = message;
                UpdatedAt = DateTimeOffset.Now;
                if (CreatedAt == default)
                {
                    CreatedAt = record.CreatedAt;
                }
            }
        }

        public OpsTaskRow ToRow(string publicBaseUrl)
        {
            lock (gate)
            {
                return new OpsTaskRow
                {
                    TaskId = TaskId,
                    DeviceId = DeviceId,
                    SiteId = SiteId,
                    CabinetId = CabinetId,
                    Stage = Stage,
                    Status = Status,
                    Message = Message,
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    TerminalCount = TerminalCount,
                    WireTagCount = WireTagCount,
                    MatchedCount = MatchedCount,
                    MismatchCount = MismatchCount,
                    UnrecognizedCount = UnrecognizedCount,
                    ResultJsonUrl = RewriteFileUrl(ResultJsonUrl, publicBaseUrl),
                    ReportUrl = RewriteFileUrl(ReportUrl, publicBaseUrl),
                    VisualSummaryUrl = RewriteFileUrl(VisualSummaryUrl, publicBaseUrl)
                };
            }
        }
    }
}
