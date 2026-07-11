using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MqttVision.Server.Configuration;

public sealed class AdminConfigurationBackupService : IDisposable
{
    public const string CurrentSchemaVersion = "1.0";

    private static readonly Action<ILogger, string, Exception?> BackupFileReadFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(BackupFileReadFailed)),
            "配置备份文件无法读取。Path={Path}");

    private static readonly Action<ILogger, string, bool, int, Exception?> BackupRestored =
        LoggerMessage.Define<string, bool, int>(
            LogLevel.Information,
            new EventId(2, nameof(BackupRestored)),
            "管理员配置备份已恢复。BackupId={BackupId}, Runtime={Runtime}, Cabinets={CabinetCount}");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly RuntimeConfigurationService runtimeConfiguration;
    private readonly CabinetConfigurationAdminService cabinetConfiguration;
    private readonly ServerPathInitializer pathInitializer;
    private readonly IHostEnvironment environment;
    private readonly ILogger<AdminConfigurationBackupService> logger;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public AdminConfigurationBackupService(
        RuntimeConfigurationService runtimeConfiguration,
        CabinetConfigurationAdminService cabinetConfiguration,
        ServerPathInitializer pathInitializer,
        IHostEnvironment environment,
        ILogger<AdminConfigurationBackupService> logger)
    {
        this.runtimeConfiguration = runtimeConfiguration;
        this.cabinetConfiguration = cabinetConfiguration;
        this.pathInitializer = pathInitializer;
        this.environment = environment;
        this.logger = logger;
        BackupRoot = ResolveBackupRoot();
    }

    public string BackupRoot { get; }

    public async Task<IReadOnlyList<AdminConfigurationBackupSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(BackupRoot))
        {
            return [];
        }

        var summaries = new List<AdminConfigurationBackupSummary>();
        foreach (var path in Directory.EnumerateFiles(BackupRoot, "backup-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await ReadDocumentFromPathAsync(path, cancellationToken);
                summaries.Add(BuildSummary(Path.GetFileNameWithoutExtension(path), path, document));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                BackupFileReadFailed(logger, path, ex);
            }
        }

        return summaries
            .OrderByDescending(summary => summary.CreatedAt)
            .ToArray();
    }

    public async Task<AdminConfigurationBackupCreateResult> CreateAsync(
        AdminConfigurationBackupCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IncludeRuntime && !request.IncludeCabinets)
        {
            return new AdminConfigurationBackupCreateResult(
                false,
                "至少需要选择一种备份内容。",
                null,
                [new ConfigurationValidationIssue("scope", "至少需要选择系统配置或柜体配置。")]);
        }

        await operationLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateCoreAsync(request, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AdminConfigurationBackupRestorePlan> GetRestorePlanAsync(
        string backupId,
        AdminConfigurationBackupRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var document = await ReadDocumentAsync(backupId, cancellationToken);
            return BuildRestorePlan(backupId, document, request);
        }
        catch (InvalidOperationException ex)
        {
            return new AdminConfigurationBackupRestorePlan(
                false,
                backupId,
                DateTimeOffset.MinValue,
                string.Empty,
                false,
                0,
                0,
                [new ConfigurationValidationIssue("backupId", ex.Message)]);
        }
    }

    public async Task<AdminConfigurationBackupRestoreResult> RestoreAsync(
        string backupId,
        AdminConfigurationBackupRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(backupId, cancellationToken);
            var plan = BuildRestorePlan(backupId, document, request);
            if (!plan.CanRestore)
            {
                return new AdminConfigurationBackupRestoreResult(
                    false,
                    "备份预检未通过，未恢复。",
                    plan,
                    null,
                    null,
                    [],
                    plan.Issues);
            }

            AdminConfigurationBackupSummary? safetyBackup = null;
            if (request.CreateSafetyBackup)
            {
                var safetyResult = await CreateCoreAsync(
                    new AdminConfigurationBackupCreateRequest
                    {
                        Description = $"恢复 {backupId} 前自动备份",
                        IncludeRuntime = true,
                        IncludeCabinets = true
                    },
                    cancellationToken);
                safetyBackup = safetyResult.Backup;
            }

            RuntimeConfigurationSaveResult? runtimeResult = null;
            if (request.IncludeRuntime && document.RuntimeConfiguration is not null)
            {
                runtimeResult = await runtimeConfiguration.SaveAsync(
                    document.RuntimeConfiguration.Configuration,
                    cancellationToken);
                if (!runtimeResult.Success)
                {
                    return new AdminConfigurationBackupRestoreResult(
                        false,
                        runtimeResult.Message,
                        plan,
                        safetyBackup,
                        runtimeResult,
                        [],
                        runtimeResult.Snapshot.Validation.Issues);
                }
            }

            var cabinetResults = new List<CabinetConfigurationSaveResult>();
            if (request.IncludeCabinets)
            {
                foreach (var cabinet in document.Cabinets)
                {
                    var result = await cabinetConfiguration.SaveAsync(
                        cabinet.Configuration,
                        cancellationToken);
                    cabinetResults.Add(result);
                    if (!result.Success)
                    {
                        return new AdminConfigurationBackupRestoreResult(
                            false,
                            $"柜体 {cabinet.CabinetId} 恢复失败。",
                            plan,
                            safetyBackup,
                            runtimeResult,
                            cabinetResults,
                            PrefixCabinetIssues(cabinet.CabinetId, result.Issues));
                    }
                }
            }

            BackupRestored(
                logger,
                backupId,
                request.IncludeRuntime && document.RuntimeConfiguration is not null,
                request.IncludeCabinets ? document.Cabinets.Count : 0,
                null);

            return new AdminConfigurationBackupRestoreResult(
                true,
                BuildRestoreMessage(request, document),
                plan,
                safetyBackup,
                runtimeResult,
                cabinetResults,
                plan.Issues);
        }
        catch (InvalidOperationException ex)
        {
            var plan = new AdminConfigurationBackupRestorePlan(
                false,
                backupId,
                DateTimeOffset.MinValue,
                string.Empty,
                false,
                0,
                0,
                [new ConfigurationValidationIssue("backupId", ex.Message)]);
            return new AdminConfigurationBackupRestoreResult(
                false,
                ex.Message,
                plan,
                null,
                null,
                [],
                plan.Issues);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<byte[]> ReadBackupBytesAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveBackupPath(backupId);
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private async Task<AdminConfigurationBackupCreateResult> CreateCoreAsync(
        AdminConfigurationBackupCreateRequest request,
        CancellationToken cancellationToken)
    {
        var issues = new List<ConfigurationValidationIssue>();
        var document = await BuildDocumentAsync(request, issues, cancellationToken);
        Directory.CreateDirectory(BackupRoot);

        var backupId = BuildBackupId(document.ExportedAt);
        var path = BuildBackupPath(backupId);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        var summary = BuildSummary(backupId, path, document);
        return new AdminConfigurationBackupCreateResult(
            true,
            "配置备份已创建。",
            summary,
            issues);
    }

    private async Task<AdminConfigurationBackupDocument> BuildDocumentAsync(
        AdminConfigurationBackupCreateRequest request,
        ICollection<ConfigurationValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var document = new AdminConfigurationBackupDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Description = NormalizeDescription(request.Description)
        };

        if (request.IncludeRuntime)
        {
            var snapshot = runtimeConfiguration.GetSnapshot();
            document.RuntimeConfiguration = new RuntimeConfigurationBackupEntry
            {
                Version = snapshot.Version,
                LocalConfigPath = snapshot.LocalConfigPath,
                Configuration = AdminConfigurationForm.FromOptions(snapshot.Options)
            };
        }

        if (request.IncludeCabinets)
        {
            var summaries = await cabinetConfiguration.ListAsync(cancellationToken);
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var cabinet = await cabinetConfiguration.GetAsync(summary.CabinetId, cancellationToken);
                    document.Cabinets.Add(new CabinetConfigurationBackupEntry
                    {
                        CabinetId = cabinet.CabinetId,
                        FilePath = summary.FilePath,
                        UpdatedAt = summary.UpdatedAt,
                        Configuration = cabinet
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    issues.Add(new ConfigurationValidationIssue(
                        $"cabinets:{summary.CabinetId}",
                        $"柜体 {summary.CabinetId} 无法备份：{ex.Message}",
                        true));
                }
            }
        }

        return document;
    }

    private AdminConfigurationBackupRestorePlan BuildRestorePlan(
        string backupId,
        AdminConfigurationBackupDocument document,
        AdminConfigurationBackupRestoreRequest request)
    {
        var issues = new List<ConfigurationValidationIssue>();
        ValidateDocument(document, issues);
        if (!request.IncludeRuntime && !request.IncludeCabinets)
        {
            issues.Add(new ConfigurationValidationIssue("scope", "至少需要选择一种恢复内容。"));
        }

        var runtimeChangedFieldCount = 0;
        if (request.IncludeRuntime)
        {
            if (document.RuntimeConfiguration is null)
            {
                issues.Add(new ConfigurationValidationIssue("runtime", "备份文件不包含系统配置。"));
            }
            else
            {
                var options = document.RuntimeConfiguration.Configuration.ToOptions();
                issues.AddRange(runtimeConfiguration.Validate(options).Issues);
                runtimeChangedFieldCount = CountRuntimeChangedFields(options);
            }
        }

        if (request.IncludeCabinets)
        {
            if (document.Cabinets.Count == 0)
            {
                issues.Add(new ConfigurationValidationIssue("cabinets", "备份文件不包含柜体配置。", true));
            }

            foreach (var cabinet in document.Cabinets)
            {
                if (!cabinet.CabinetId.Equals(cabinet.Configuration.CabinetId, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ConfigurationValidationIssue(
                        $"cabinets:{cabinet.CabinetId}",
                        $"柜体 {cabinet.CabinetId} 的备份元数据与配置内容不一致。"));
                    continue;
                }

                issues.AddRange(PrefixCabinetIssues(
                    cabinet.CabinetId,
                    cabinetConfiguration.Validate(cabinet.Configuration.ToDomain())));
            }
        }

        if (!request.AllowWarnings && issues.Any(issue => issue.IsWarning))
        {
            issues.Add(new ConfigurationValidationIssue("warnings", "当前恢复选项不允许带警告恢复。"));
        }

        return new AdminConfigurationBackupRestorePlan(
            !issues.Any(issue => !issue.IsWarning),
            backupId,
            document.ExportedAt,
            document.Description,
            document.RuntimeConfiguration is not null,
            runtimeChangedFieldCount,
            document.Cabinets.Count,
            issues);
    }

    private static void ValidateDocument(
        AdminConfigurationBackupDocument document,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (!document.SchemaVersion.Equals(CurrentSchemaVersion, StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationValidationIssue("schemaVersion", "备份文件版本不受支持。"));
        }
    }

    private int CountRuntimeChangedFields(MqttVisionServerOptions backupOptions)
    {
        var currentForm = AdminConfigurationForm.FromOptions(runtimeConfiguration.Current);
        var backupForm = AdminConfigurationForm.FromOptions(backupOptions);
        var current = JsonSerializer.Serialize(currentForm, JsonOptions);
        var backup = JsonSerializer.Serialize(backupForm, JsonOptions);
        if (current.Equals(backup, StringComparison.Ordinal))
        {
            return 0;
        }

        return RuntimeConfigurationService.GetFieldDescriptors()
            .Count(field => HasRuntimeFieldChanged(field.Path, currentForm, backupForm));
    }

    private static bool HasRuntimeFieldChanged(
        string path,
        AdminConfigurationForm current,
        AdminConfigurationForm backup) =>
        path switch
        {
            "MqttVision:PublicBaseUrl" => current.PublicBaseUrl != backup.PublicBaseUrl,
            "MqttVision:StorageRoot" => current.StorageRoot != backup.StorageRoot,
            "MqttVision:MaxUploadBytes" => current.MaxUploadBytes != backup.MaxUploadBytes,
            "MqttVision:Mqtt:BrokerHost" => current.Mqtt.BrokerHost != backup.Mqtt.BrokerHost,
            "MqttVision:Mqtt:BrokerPort" => current.Mqtt.BrokerPort != backup.Mqtt.BrokerPort,
            "MqttVision:Mqtt:UserName" => current.Mqtt.UserName != backup.Mqtt.UserName,
            "MqttVision:Mqtt:Password" => current.Mqtt.Password != backup.Mqtt.Password,
            "MqttVision:Mqtt:ClientId" => current.Mqtt.ClientId != backup.Mqtt.ClientId,
            "MqttVision:Mqtt:TaskSubmitTopic" => current.Mqtt.TaskSubmitTopic != backup.Mqtt.TaskSubmitTopic,
            "MqttVision:Mqtt:TaskProgressTopicTemplate" => current.Mqtt.TaskProgressTopicTemplate != backup.Mqtt.TaskProgressTopicTemplate,
            "MqttVision:Mqtt:TaskResultTopicTemplate" => current.Mqtt.TaskResultTopicTemplate != backup.Mqtt.TaskResultTopicTemplate,
            "MqttVision:Processing:EnablePlaceholderPipeline" => current.Processing.EnablePlaceholderPipeline != backup.Processing.EnablePlaceholderPipeline,
            "MqttVision:Processing:YoloOnnxModelPath" => current.Processing.YoloOnnxModelPath != backup.Processing.YoloOnnxModelPath,
            "MqttVision:Processing:PaddleOcrModelDirectory" => current.Processing.PaddleOcrModelDirectory != backup.Processing.PaddleOcrModelDirectory,
            "MqttVision:Processing:PaddleOcrEnabled" => current.Processing.PaddleOcrEnabled != backup.Processing.PaddleOcrEnabled,
            "MqttVision:Processing:PaddleOcrDeploymentMode" => current.Processing.PaddleOcrDeploymentMode != backup.Processing.PaddleOcrDeploymentMode,
            "MqttVision:Processing:PaddleOcrServiceUrl" => current.Processing.PaddleOcrServiceUrl != backup.Processing.PaddleOcrServiceUrl,
            "MqttVision:Processing:PaddleOcrVisualize" => current.Processing.PaddleOcrVisualize != backup.Processing.PaddleOcrVisualize,
            "MqttVision:Processing:PaddleOcrFileType" => current.Processing.PaddleOcrFileType != backup.Processing.PaddleOcrFileType,
            "MqttVision:Processing:PaddleOcrUseDocOrientationClassify" => current.Processing.PaddleOcrUseDocOrientationClassify != backup.Processing.PaddleOcrUseDocOrientationClassify,
            "MqttVision:Processing:PaddleOcrUseDocUnwarping" => current.Processing.PaddleOcrUseDocUnwarping != backup.Processing.PaddleOcrUseDocUnwarping,
            "MqttVision:Processing:PaddleOcrUseTextlineOrientation" => current.Processing.PaddleOcrUseTextlineOrientation != backup.Processing.PaddleOcrUseTextlineOrientation,
            "MqttVision:Processing:PaddleOcrCommand" => current.Processing.PaddleOcrCommand != backup.Processing.PaddleOcrCommand,
            "MqttVision:Processing:PaddleOcrArgumentsTemplate" => current.Processing.PaddleOcrArgumentsTemplate != backup.Processing.PaddleOcrArgumentsTemplate,
            "MqttVision:Processing:PaddleOcrWorkingDirectory" => current.Processing.PaddleOcrWorkingDirectory != backup.Processing.PaddleOcrWorkingDirectory,
            "MqttVision:Processing:PaddleOcrAdditionalPath" => current.Processing.PaddleOcrAdditionalPath != backup.Processing.PaddleOcrAdditionalPath,
            "MqttVision:Processing:PaddleOcrMinimumTextScore" => current.Processing.PaddleOcrMinimumTextScore != backup.Processing.PaddleOcrMinimumTextScore,
            "MqttVision:Processing:PaddleOcrTimeoutSeconds" => current.Processing.PaddleOcrTimeoutSeconds != backup.Processing.PaddleOcrTimeoutSeconds,
            "MqttVision:Processing:CabinetConfigurationRoot" => current.Processing.CabinetConfigurationRoot != backup.Processing.CabinetConfigurationRoot,
            "MqttVision:Processing:PairMaxDistancePixels" => current.Processing.PairMaxDistancePixels != backup.Processing.PairMaxDistancePixels,
            "MqttVision:Processing:AmbiguousDistanceTolerancePixels" => current.Processing.AmbiguousDistanceTolerancePixels != backup.Processing.AmbiguousDistanceTolerancePixels,
            "MqttVision:Processing:PairMaxHorizontalDistancePixels" => current.Processing.PairMaxHorizontalDistancePixels != backup.Processing.PairMaxHorizontalDistancePixels,
            "MqttVision:Processing:PairMaxVerticalGapPixels" => current.Processing.PairMaxVerticalGapPixels != backup.Processing.PairMaxVerticalGapPixels,
            "MqttVision:Processing:YoloInputSize" => current.Processing.YoloInputSize != backup.Processing.YoloInputSize,
            "MqttVision:Processing:ConfidenceThreshold" => current.Processing.ConfidenceThreshold != backup.Processing.ConfidenceThreshold,
            "MqttVision:Processing:NmsThreshold" => current.Processing.NmsThreshold != backup.Processing.NmsThreshold,
            _ => false
        };

    private async Task<AdminConfigurationBackupDocument> ReadDocumentAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var path = ResolveBackupPath(backupId);
        return await ReadDocumentFromPathAsync(path, cancellationToken);
    }

    private static async Task<AdminConfigurationBackupDocument> ReadDocumentFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<AdminConfigurationBackupDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? throw new InvalidOperationException("备份文件内容为空。");
    }

    private string ResolveBackupPath(string backupId)
    {
        var path = BuildBackupPath(backupId);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("备份文件不存在。");
        }

        return path;
    }

    private string BuildBackupPath(string backupId)
    {
        var safeBackupId = NormalizeBackupId(backupId);
        var path = Path.GetFullPath(Path.Combine(BackupRoot, $"{safeBackupId}.json"));
        var root = Path.GetFullPath(BackupRoot);
        if (!IsSameOrChildPath(path, root))
        {
            throw new InvalidOperationException("备份编号不合法。");
        }

        return path;
    }

    private string ResolveBackupRoot()
    {
        var defaultRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, ".admin-backups"));
        var storageRoot = Path.GetFullPath(pathInitializer.StorageRoot);
        return IsSameOrChildPath(defaultRoot, storageRoot)
            ? Path.Combine(GetApplicationDataRoot(), "MqttVision.Server", HashPath(environment.ContentRootPath), "admin-backups")
            : defaultRoot;
    }

    private static string GetApplicationDataRoot()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return localApplicationData;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Path.Combine(Path.GetTempPath(), "MqttVision")
            : Path.Combine(userProfile, ".mqttvision");
    }

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string NormalizeBackupId(string backupId)
    {
        var normalized = backupId.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > 96 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new InvalidOperationException("备份编号不合法。");
        }

        return normalized;
    }

    private static string BuildBackupId(DateTimeOffset exportedAt) =>
        $"backup-{exportedAt.ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32];

    private static AdminConfigurationBackupSummary BuildSummary(
        string backupId,
        string path,
        AdminConfigurationBackupDocument document)
    {
        var fileInfo = new FileInfo(path);
        return new AdminConfigurationBackupSummary(
            backupId,
            document.ExportedAt,
            document.Description,
            document.RuntimeConfiguration is not null,
            document.Cabinets.Count,
            fileInfo.Exists ? fileInfo.Length : 0,
            FormatSize(fileInfo.Exists ? fileInfo.Length : 0),
            path);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024)
        {
            return $"{kilobytes:0.#} KB";
        }

        var megabytes = kilobytes / 1024d;
        return $"{megabytes:0.##} MB";
    }

    private static string NormalizeDescription(string? description)
    {
        var normalized = description?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "管理员手动备份"
            : normalized.Length <= 120
                ? normalized
                : normalized[..120];
    }

    private static IReadOnlyList<ConfigurationValidationIssue> PrefixCabinetIssues(
        string cabinetId,
        IEnumerable<ConfigurationValidationIssue> issues) =>
        issues
            .Select(issue => new ConfigurationValidationIssue(
                $"cabinets:{cabinetId}:{issue.Path}",
                $"柜体 {cabinetId}：{issue.Message}",
                issue.IsWarning))
            .ToArray();

    private static string BuildRestoreMessage(
        AdminConfigurationBackupRestoreRequest request,
        AdminConfigurationBackupDocument document)
    {
        var parts = new List<string> { "备份已恢复" };
        if (request.IncludeRuntime && document.RuntimeConfiguration is not null)
        {
            parts.Add("系统配置已写入本地覆盖文件");
        }

        if (request.IncludeCabinets)
        {
            parts.Add($"{document.Cabinets.Count.ToString(CultureInfo.InvariantCulture)} 个柜体配置已写入");
        }

        return string.Join("，", parts) + "。";
    }

    public void Dispose()
    {
        operationLock.Dispose();
    }
}
