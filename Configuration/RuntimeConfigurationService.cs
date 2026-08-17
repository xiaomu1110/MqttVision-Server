using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace MqttVision.Server.Configuration;

public sealed class RuntimeConfigurationService : IDisposable
{
    private static readonly IReadOnlyList<ConfigurationFieldDescriptor> FieldDescriptors =
    [
        new("MqttVision:PublicBaseUrl", "公开访问地址", "服务端", ConfigurationApplyMode.HotReload, "新任务生成结果链接时立即使用。"),
        new("MqttVision:StorageRoot", "运行时存储目录", "服务端", ConfigurationApplyMode.RequiresRestart, "静态文件映射在启动时固定，修改后需要重启服务。"),
        new("MqttVision:MaxUploadBytes", "上传大小上限", "服务端", ConfigurationApplyMode.HotReload, "新上传请求立即使用。"),
        new("MqttVision:CadImport:MaxConcurrentParsers", "CAD 最大并发解析数", "CAD 导入", ConfigurationApplyMode.HotReload, "最多同时解析 3 个 CAD 文件。"),
        new("MqttVision:CadImport:MaxFileBytes", "CAD 文件大小上限", "CAD 导入", ConfigurationApplyMode.HotReload, "单个 CAD 文件超过该大小时拒绝导入。"),
        new("MqttVision:CadImport:ParserTimeoutSeconds", "CAD 解析超时", "CAD 导入", ConfigurationApplyMode.HotReload, "单个 CAD 文件超过该时间仍未完成则标记失败。"),
        new("MqttVision:Mqtt:BrokerHost", "消息服务器地址", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重连 MQTT。"),
        new("MqttVision:Mqtt:BrokerPort", "消息服务器端口", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重连 MQTT。"),
        new("MqttVision:Mqtt:UserName", "消息服务器用户名", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重连 MQTT。"),
        new("MqttVision:Mqtt:Password", "消息服务器密码", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重连 MQTT。"),
        new("MqttVision:Mqtt:ClientId", "服务端客户端编号", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重连 MQTT。"),
        new("MqttVision:Mqtt:TaskSubmitTopic", "任务提交主题", "消息队列", ConfigurationApplyMode.RequiresReconnect, "保存后服务端自动重新订阅主题。"),
        new("MqttVision:Mqtt:TaskProgressTopicTemplate", "进度发布主题", "消息队列", ConfigurationApplyMode.RequiresReconnect, "发布端会在下次发布前使用新主题。"),
        new("MqttVision:Mqtt:TaskResultTopicTemplate", "结果发布主题", "消息队列", ConfigurationApplyMode.RequiresReconnect, "发布端会在下次发布前使用新主题。"),
        new("MqttVision:Processing:EnablePlaceholderPipeline", "占位检测模式", "检测流程", ConfigurationApplyMode.HotReload, "新任务立即使用。"),
        new("MqttVision:Processing:YoloOnnxModelPath", "目标检测模型文件", "检测流程", ConfigurationApplyMode.RequiresModelReload, "新检测任务会自动加载对应模型。"),
        new("MqttVision:Processing:PaddleOcrModelDirectory", "文字识别模型目录", "文字识别", ConfigurationApplyMode.HotReload, "保留给命令行文字识别实现使用。"),
        new("MqttVision:Processing:PaddleOcrEnabled", "文字识别开关", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrDeploymentMode", "文字识别部署模式", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrServiceUrl", "文字识别服务地址", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrVisualize", "文字识别可视化", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrFileType", "文字识别文件类型", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrUseDocOrientationClassify", "文档方向分类", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrUseDocUnwarping", "文档矫正", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrUseTextlineOrientation", "文本行方向识别", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrCommand", "命令行识别命令", "文字识别", ConfigurationApplyMode.HotReload, "保留给命令行文字识别实现使用。"),
        new("MqttVision:Processing:PaddleOcrArgumentsTemplate", "命令行识别参数模板", "文字识别", ConfigurationApplyMode.HotReload, "保留给命令行文字识别实现使用。"),
        new("MqttVision:Processing:PaddleOcrWorkingDirectory", "命令行识别工作目录", "文字识别", ConfigurationApplyMode.HotReload, "保留给命令行文字识别实现使用。"),
        new("MqttVision:Processing:PaddleOcrAdditionalPath", "命令行识别附加路径", "文字识别", ConfigurationApplyMode.HotReload, "保留给命令行文字识别实现使用。"),
        new("MqttVision:Processing:PaddleOcrMinimumTextScore", "文字识别最低分数", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:PaddleOcrTimeoutSeconds", "文字识别超时秒数", "文字识别", ConfigurationApplyMode.HotReload, "新 OCR 请求立即使用。"),
        new("MqttVision:Processing:CabinetConfigurationRoot", "柜体配置目录", "检测流程", ConfigurationApplyMode.HotReload, "新任务加载柜体配置时立即使用。"),
        new("MqttVision:Processing:PairMaxDistancePixels", "配对最大距离", "检测流程", ConfigurationApplyMode.HotReload, "新任务立即使用。"),
        new("MqttVision:Processing:AmbiguousDistanceTolerancePixels", "模糊距离容差", "检测流程", ConfigurationApplyMode.HotReload, "新任务立即使用。"),
        new("MqttVision:Processing:PairMaxHorizontalDistancePixels", "水平最大距离", "检测流程", ConfigurationApplyMode.HotReload, "新任务立即使用。"),
        new("MqttVision:Processing:PairMaxVerticalGapPixels", "垂直最大间距", "检测流程", ConfigurationApplyMode.HotReload, "新任务立即使用。"),
        new("MqttVision:Processing:YoloInputSize", "模型输入尺寸", "检测流程", ConfigurationApplyMode.RequiresModelReload, "新检测任务会自动使用。"),
        new("MqttVision:Processing:ConfidenceThreshold", "检测置信度阈值", "检测流程", ConfigurationApplyMode.RequiresModelReload, "新检测任务会自动使用。"),
        new("MqttVision:Processing:NmsThreshold", "重叠去除阈值", "检测流程", ConfigurationApplyMode.RequiresModelReload, "新检测任务会自动使用。")
    ];

    private readonly SemaphoreSlim saveLock = new(1, 1);
    private readonly ILogger<RuntimeConfigurationService> logger;
    private readonly string contentRootPath;
    private MqttVisionServerOptions current;
    private long version = 1;
    private DateTimeOffset updatedAt;

    public RuntimeConfigurationService(
        IOptions<MqttVisionServerOptions> options,
        IHostEnvironment environment,
        ILogger<RuntimeConfigurationService> logger)
    {
        this.logger = logger;
        contentRootPath = environment.ContentRootPath;
        current = options.Value;
        updatedAt = DateTimeOffset.Now;
        LocalConfigPath = MqttVisionYamlConfiguration.ResolveWritableLocalConfigPath(contentRootPath);
    }

    public event EventHandler<RuntimeConfigurationChangedEventArgs>? Changed;

    public string LocalConfigPath { get; }

    public MqttVisionServerOptions Current => Volatile.Read(ref current);

    public RuntimeConfigurationSnapshot GetSnapshot()
    {
        var options = Current;
        return new RuntimeConfigurationSnapshot(
            Interlocked.Read(ref version),
            updatedAt,
            LocalConfigPath,
            options,
            Validate(options),
            FieldDescriptors);
    }

    public RuntimeConfigurationValidationResult Validate(MqttVisionServerOptions options)
    {
        var issues = new List<ConfigurationValidationIssue>();
        AddRequired(issues, "MqttVision:PublicBaseUrl", "公开访问地址不能为空。", options.PublicBaseUrl);
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) &&
            (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
                publicUri.Scheme is not ("http" or "https")))
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:PublicBaseUrl", "公开访问地址必须是 http 或 https 开头的完整地址。"));
        }

        AddRequired(issues, "MqttVision:StorageRoot", "运行时存储目录不能为空。", options.StorageRoot);
        if (options.MaxUploadBytes < 1024 * 1024)
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:MaxUploadBytes", "上传大小上限不能小于 1 MB。"));
        }

        ValidateMqtt(options.Mqtt, issues);
        ValidateCadImport(options.CadImport, issues);
        ValidateProcessing(options.Processing, issues);
        return issues.Count == 0
            ? RuntimeConfigurationValidationResult.Valid
            : new RuntimeConfigurationValidationResult(issues);
    }

    public async Task<RuntimeConfigurationSaveResult> SaveAsync(
        AdminConfigurationForm form,
        CancellationToken cancellationToken = default)
    {
        var next = form.ToOptions();
        var validation = Validate(next);
        if (!validation.IsValid)
        {
            return new RuntimeConfigurationSaveResult(
                false,
                "配置校验未通过，未保存。",
                GetSnapshot() with { Validation = validation },
                []);
        }

        await saveLock.WaitAsync(cancellationToken);
        try
        {
            var previous = Current;
            var changedPaths = FindChangedPaths(previous, next);
            if (changedPaths.Count == 0)
            {
                return new RuntimeConfigurationSaveResult(
                    true,
                    "配置未变化。",
                    GetSnapshot(),
                    changedPaths);
            }

            await WriteLocalYamlAsync(next, cancellationToken);
            Volatile.Write(ref current, next);
            var nextVersion = Interlocked.Increment(ref version);
            updatedAt = DateTimeOffset.Now;
            Changed?.Invoke(
                this,
                new RuntimeConfigurationChangedEventArgs(previous, next, changedPaths, nextVersion));

            logger.LogInformation(
                "运行时配置已保存。Version={Version}, Changed={ChangedPaths}",
                nextVersion,
                string.Join(",", changedPaths));

            return new RuntimeConfigurationSaveResult(
                true,
                BuildSaveMessage(changedPaths),
                GetSnapshot(),
                changedPaths);
        }
        finally
        {
            saveLock.Release();
        }
    }

    public static IReadOnlyList<ConfigurationFieldDescriptor> GetFieldDescriptors() => FieldDescriptors;

    private static string BuildSaveMessage(IReadOnlyCollection<string> changedPaths)
    {
        var changedModes = FieldDescriptors
            .Where(field => changedPaths.Contains(field.Path, StringComparer.OrdinalIgnoreCase))
            .Select(field => field.ApplyMode)
            .Distinct()
            .ToArray();
        var messages = new List<string> { "配置已保存" };
        if (changedModes.Contains(ConfigurationApplyMode.HotReload))
        {
            messages.Add("热生效项已对新任务生效");
        }

        if (changedModes.Contains(ConfigurationApplyMode.RequiresReconnect))
        {
            messages.Add("MQTT 将在后台自动重连");
        }

        if (changedModes.Contains(ConfigurationApplyMode.RequiresModelReload))
        {
            messages.Add("模型相关配置会在新检测任务中自动加载");
        }

        if (changedModes.Contains(ConfigurationApplyMode.RequiresRestart))
        {
            messages.Add("存储或服务级配置需要重启服务");
        }

        return string.Join("，", messages) + "。";
    }

    private async Task WriteLocalYamlAsync(
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(LocalConfigPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var yaml = BuildLocalYaml(options);
        var tempPath = $"{LocalConfigPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, yaml, Encoding.UTF8, cancellationToken);
        if (File.Exists(LocalConfigPath))
        {
            File.Replace(tempPath, LocalConfigPath, $"{LocalConfigPath}.bak", true);
        }
        else
        {
            File.Move(tempPath, LocalConfigPath);
        }
    }

    private static string BuildLocalYaml(MqttVisionServerOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 管理员后台生成的本地覆盖配置。");
        builder.AppendLine("# 此文件可能包含 MQTT 密码，已在 .gitignore 中忽略。");
        builder.AppendLine("MqttVision:");
        Append(builder, 1, "PublicBaseUrl", options.PublicBaseUrl);
        Append(builder, 1, "StorageRoot", options.StorageRoot);
        Append(builder, 1, "MaxUploadBytes", options.MaxUploadBytes);
        builder.AppendLine("  CadImport:");
        Append(builder, 2, "MaxConcurrentParsers", options.CadImport.MaxConcurrentParsers);
        Append(builder, 2, "MaxFileBytes", options.CadImport.MaxFileBytes);
        Append(builder, 2, "ParserTimeoutSeconds", options.CadImport.ParserTimeoutSeconds);
        builder.AppendLine("    AllowedExtensions:");
        foreach (var extension in options.CadImport.AllowedExtensions)
        {
            builder.Append("      - ");
            builder.AppendLine(Quote(extension));
        }
        builder.AppendLine("  Mqtt:");
        Append(builder, 2, "BrokerHost", options.Mqtt.BrokerHost);
        Append(builder, 2, "BrokerPort", options.Mqtt.BrokerPort);
        Append(builder, 2, "UserName", options.Mqtt.UserName);
        Append(builder, 2, "Password", options.Mqtt.Password);
        Append(builder, 2, "ClientId", options.Mqtt.ClientId);
        Append(builder, 2, "TaskSubmitTopic", options.Mqtt.TaskSubmitTopic);
        Append(builder, 2, "TaskProgressTopicTemplate", options.Mqtt.TaskProgressTopicTemplate);
        Append(builder, 2, "TaskResultTopicTemplate", options.Mqtt.TaskResultTopicTemplate);
        builder.AppendLine("  Processing:");
        Append(builder, 2, "EnablePlaceholderPipeline", options.Processing.EnablePlaceholderPipeline);
        Append(builder, 2, "YoloOnnxModelPath", options.Processing.YoloOnnxModelPath);
        Append(builder, 2, "PaddleOcrModelDirectory", options.Processing.PaddleOcrModelDirectory);
        Append(builder, 2, "PaddleOcrEnabled", options.Processing.PaddleOcrEnabled);
        Append(builder, 2, "PaddleOcrDeploymentMode", options.Processing.PaddleOcrDeploymentMode);
        Append(builder, 2, "PaddleOcrServiceUrl", options.Processing.PaddleOcrServiceUrl);
        Append(builder, 2, "PaddleOcrVisualize", options.Processing.PaddleOcrVisualize);
        Append(builder, 2, "PaddleOcrFileType", options.Processing.PaddleOcrFileType);
        Append(builder, 2, "PaddleOcrUseDocOrientationClassify", options.Processing.PaddleOcrUseDocOrientationClassify);
        Append(builder, 2, "PaddleOcrUseDocUnwarping", options.Processing.PaddleOcrUseDocUnwarping);
        Append(builder, 2, "PaddleOcrUseTextlineOrientation", options.Processing.PaddleOcrUseTextlineOrientation);
        Append(builder, 2, "PaddleOcrCommand", options.Processing.PaddleOcrCommand);
        Append(builder, 2, "PaddleOcrArgumentsTemplate", options.Processing.PaddleOcrArgumentsTemplate);
        Append(builder, 2, "PaddleOcrWorkingDirectory", options.Processing.PaddleOcrWorkingDirectory);
        Append(builder, 2, "PaddleOcrAdditionalPath", options.Processing.PaddleOcrAdditionalPath);
        Append(builder, 2, "PaddleOcrMinimumTextScore", options.Processing.PaddleOcrMinimumTextScore);
        Append(builder, 2, "PaddleOcrTimeoutSeconds", options.Processing.PaddleOcrTimeoutSeconds);
        Append(builder, 2, "CabinetConfigurationRoot", options.Processing.CabinetConfigurationRoot);
        Append(builder, 2, "PairMaxDistancePixels", options.Processing.PairMaxDistancePixels);
        Append(builder, 2, "AmbiguousDistanceTolerancePixels", options.Processing.AmbiguousDistanceTolerancePixels);
        Append(builder, 2, "PairMaxHorizontalDistancePixels", options.Processing.PairMaxHorizontalDistancePixels);
        Append(builder, 2, "PairMaxVerticalGapPixels", options.Processing.PairMaxVerticalGapPixels);
        Append(builder, 2, "YoloInputSize", options.Processing.YoloInputSize);
        Append(builder, 2, "ConfidenceThreshold", options.Processing.ConfidenceThreshold);
        Append(builder, 2, "NmsThreshold", options.Processing.NmsThreshold);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, int indentLevel, string key, string? value)
    {
        builder.Append(new string(' ', indentLevel * 2));
        builder.Append(key);
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "null" : Quote(value));
    }

    private static void Append(StringBuilder builder, int indentLevel, string key, bool value) =>
        AppendRaw(builder, indentLevel, key, value ? "true" : "false");

    private static void Append(StringBuilder builder, int indentLevel, string key, bool? value) =>
        AppendRaw(builder, indentLevel, key, value.HasValue ? value.Value ? "true" : "false" : "null");

    private static void Append(StringBuilder builder, int indentLevel, string key, int value) =>
        AppendRaw(builder, indentLevel, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, int indentLevel, string key, long value) =>
        AppendRaw(builder, indentLevel, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, int indentLevel, string key, double value) =>
        AppendRaw(builder, indentLevel, key, value.ToString("0.########", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, int indentLevel, string key, float value) =>
        AppendRaw(builder, indentLevel, key, value.ToString("0.########", CultureInfo.InvariantCulture));

    private static void AppendRaw(StringBuilder builder, int indentLevel, string key, string value)
    {
        builder.Append(new string(' ', indentLevel * 2));
        builder.Append(key);
        builder.Append(": ");
        builder.AppendLine(value);
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void ValidateMqtt(
        MqttOptions mqtt,
        ICollection<ConfigurationValidationIssue> issues)
    {
        AddRequired(issues, "MqttVision:Mqtt:BrokerHost", "消息服务器地址不能为空。", mqtt.BrokerHost);
        if (mqtt.BrokerPort is < 1 or > 65535)
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:Mqtt:BrokerPort", "消息服务器端口必须在 1 到 65535 之间。"));
        }

        AddRequired(issues, "MqttVision:Mqtt:ClientId", "服务端客户端编号不能为空。", mqtt.ClientId);
        AddRequired(issues, "MqttVision:Mqtt:TaskSubmitTopic", "任务提交主题不能为空。", mqtt.TaskSubmitTopic);
        AddRequired(issues, "MqttVision:Mqtt:TaskProgressTopicTemplate", "进度发布主题不能为空。", mqtt.TaskProgressTopicTemplate);
        AddRequired(issues, "MqttVision:Mqtt:TaskResultTopicTemplate", "结果发布主题不能为空。", mqtt.TaskResultTopicTemplate);
        AddSubscribeTopicValidation(issues, "MqttVision:Mqtt:TaskSubmitTopic", mqtt.TaskSubmitTopic);
        AddPublishTopicValidation(issues, "MqttVision:Mqtt:TaskProgressTopicTemplate", mqtt.TaskProgressTopicTemplate);
        AddPublishTopicValidation(issues, "MqttVision:Mqtt:TaskResultTopicTemplate", mqtt.TaskResultTopicTemplate);
        AddTopicTemplateWarning(issues, "MqttVision:Mqtt:TaskProgressTopicTemplate", mqtt.TaskProgressTopicTemplate);
        AddTopicTemplateWarning(issues, "MqttVision:Mqtt:TaskResultTopicTemplate", mqtt.TaskResultTopicTemplate);
    }

    private void ValidateProcessing(
        ProcessingOptions processing,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (processing.PaddleOcrEnabled)
        {
            AddRequired(issues, "MqttVision:Processing:PaddleOcrServiceUrl", "文字识别服务地址不能为空。", processing.PaddleOcrServiceUrl);
            if (!string.IsNullOrWhiteSpace(processing.PaddleOcrServiceUrl) &&
                (!Uri.TryCreate(processing.PaddleOcrServiceUrl, UriKind.Absolute, out var ocrUri) ||
                    ocrUri.Scheme is not ("http" or "https")))
            {
                issues.Add(new ConfigurationValidationIssue("MqttVision:Processing:PaddleOcrServiceUrl", "文字识别服务地址必须是 http 或 https 开头的完整地址。"));
            }
        }

        AddRequired(issues, "MqttVision:Processing:PaddleOcrDeploymentMode", "文字识别部署模式不能为空。", processing.PaddleOcrDeploymentMode);
        AddRequired(issues, "MqttVision:Processing:CabinetConfigurationRoot", "柜体配置目录不能为空。", processing.CabinetConfigurationRoot);
        if (!processing.EnablePlaceholderPipeline)
        {
            AddRequired(issues, "MqttVision:Processing:YoloOnnxModelPath", "关闭占位检测模式后，目标检测模型文件不能为空。", processing.YoloOnnxModelPath);
            if (!string.IsNullOrWhiteSpace(processing.YoloOnnxModelPath) &&
                !File.Exists(ResolveContentPath(processing.YoloOnnxModelPath)))
            {
                issues.Add(new ConfigurationValidationIssue("MqttVision:Processing:YoloOnnxModelPath", "目标检测模型文件不存在或服务进程无权读取。"));
            }
        }

        AddRange(issues, "MqttVision:Processing:PaddleOcrMinimumTextScore", "文字识别最低分数必须在 0 到 1 之间。", processing.PaddleOcrMinimumTextScore, 0, 1);
        AddRange(issues, "MqttVision:Processing:PaddleOcrTimeoutSeconds", "文字识别超时秒数必须在 1 到 300 之间。", processing.PaddleOcrTimeoutSeconds, 1, 300);
        AddPositive(issues, "MqttVision:Processing:PairMaxDistancePixels", "配对最大距离必须大于 0。", processing.PairMaxDistancePixels);
        AddPositive(issues, "MqttVision:Processing:AmbiguousDistanceTolerancePixels", "模糊距离容差必须大于 0。", processing.AmbiguousDistanceTolerancePixels);
        AddPositive(issues, "MqttVision:Processing:PairMaxHorizontalDistancePixels", "水平最大距离必须大于 0。", processing.PairMaxHorizontalDistancePixels);
        AddPositive(issues, "MqttVision:Processing:PairMaxVerticalGapPixels", "垂直最大间距必须大于 0。", processing.PairMaxVerticalGapPixels);
        AddRange(issues, "MqttVision:Processing:YoloInputSize", "模型输入尺寸必须在 64 到 4096 之间。", processing.YoloInputSize, 64, 4096);
        AddRange(issues, "MqttVision:Processing:ConfidenceThreshold", "检测置信度阈值必须在 0 到 1 之间。", processing.ConfidenceThreshold, 0, 1);
        AddRange(issues, "MqttVision:Processing:NmsThreshold", "重叠去除阈值必须在 0 到 1 之间。", processing.NmsThreshold, 0, 1);
    }

    private static void ValidateCadImport(
        CadImportOptions cadImport,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (cadImport.MaxConcurrentParsers is < 1 or > 3)
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:CadImport:MaxConcurrentParsers", "CAD 最大并发解析数必须在 1 到 3 之间。"));
        }

        if (cadImport.MaxFileBytes < 1024 * 1024)
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:CadImport:MaxFileBytes", "CAD 文件大小上限不能小于 1 MB。"));
        }

        if (cadImport.ParserTimeoutSeconds is < 10 or > 3600)
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:CadImport:ParserTimeoutSeconds", "CAD 解析超时必须在 10 到 3600 秒之间。"));
        }

        if (cadImport.AllowedExtensions.Length == 0 || cadImport.AllowedExtensions.Any(extension => !extension.StartsWith('.')))
        {
            issues.Add(new ConfigurationValidationIssue("MqttVision:CadImport:AllowedExtensions", "至少需要配置一个以点号开头的 CAD 扩展名。"));
        }
    }

    private static void AddRequired(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string message,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new ConfigurationValidationIssue(path, message));
        }
    }

    private static void AddTopicTemplateWarning(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string value)
    {
        if (!value.Contains("{siteId}", StringComparison.OrdinalIgnoreCase) ||
            !value.Contains("{deviceId}", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ConfigurationValidationIssue(path, "主题模板建议包含 {siteId} 和 {deviceId}，否则手机端可能收不到对应结果。", true));
        }
    }

    private static void AddSubscribeTopicValidation(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsValidMqttTopicFilter(value))
        {
            issues.Add(new ConfigurationValidationIssue(path, "任务提交主题的 MQTT 通配符位置不合法。"));
        }
    }

    private static void AddPublishTopicValidation(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Contains('+', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationValidationIssue(path, "发布主题不能包含 + 或 # 通配符。"));
            return;
        }

        var sampleTopic = value
            .Replace("{siteId}", "site", StringComparison.OrdinalIgnoreCase)
            .Replace("{deviceId}", "device", StringComparison.OrdinalIgnoreCase);
        if (!IsValidMqttTopicName(sampleTopic))
        {
            issues.Add(new ConfigurationValidationIssue(path, "发布主题格式不合法。"));
        }
    }

    private static bool IsValidMqttTopicFilter(string value)
    {
        if (value.Length == 0 || value.Length > 65535)
        {
            return false;
        }

        var levels = value.Split('/');
        for (var index = 0; index < levels.Length; index++)
        {
            var level = levels[index];
            if (level.Contains('#', StringComparison.Ordinal))
            {
                if (level != "#" || index != levels.Length - 1)
                {
                    return false;
                }
            }

            if (level.Contains('+', StringComparison.Ordinal) && level != "+")
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMqttTopicName(string value) =>
        value.Length > 0 &&
        value.Length <= 65535 &&
        !value.Contains('+', StringComparison.Ordinal) &&
        !value.Contains('#', StringComparison.Ordinal);

    private static void AddPositive(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string message,
        double value)
    {
        if (value <= 0)
        {
            issues.Add(new ConfigurationValidationIssue(path, message));
        }
    }

    private static void AddRange(
        ICollection<ConfigurationValidationIssue> issues,
        string path,
        string message,
        double value,
        double minimum,
        double maximum)
    {
        if (value < minimum || value > maximum)
        {
            issues.Add(new ConfigurationValidationIssue(path, message));
        }
    }

    private string ResolveContentPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(contentRootPath, expanded));
    }

    private static IReadOnlyList<string> FindChangedPaths(
        MqttVisionServerOptions previous,
        MqttVisionServerOptions next)
    {
        var paths = new List<string>();
        AddIfChanged(paths, "MqttVision:PublicBaseUrl", previous.PublicBaseUrl, next.PublicBaseUrl);
        AddIfChanged(paths, "MqttVision:StorageRoot", previous.StorageRoot, next.StorageRoot);
        AddIfChanged(paths, "MqttVision:MaxUploadBytes", previous.MaxUploadBytes, next.MaxUploadBytes);
        AddIfChanged(paths, "MqttVision:CadImport:MaxConcurrentParsers", previous.CadImport.MaxConcurrentParsers, next.CadImport.MaxConcurrentParsers);
        AddIfChanged(paths, "MqttVision:CadImport:MaxFileBytes", previous.CadImport.MaxFileBytes, next.CadImport.MaxFileBytes);
        AddIfChanged(paths, "MqttVision:CadImport:ParserTimeoutSeconds", previous.CadImport.ParserTimeoutSeconds, next.CadImport.ParserTimeoutSeconds);
        AddIfChanged(paths, "MqttVision:CadImport:AllowedExtensions", string.Join(',', previous.CadImport.AllowedExtensions), string.Join(',', next.CadImport.AllowedExtensions));
        AddIfChanged(paths, "MqttVision:Mqtt:BrokerHost", previous.Mqtt.BrokerHost, next.Mqtt.BrokerHost);
        AddIfChanged(paths, "MqttVision:Mqtt:BrokerPort", previous.Mqtt.BrokerPort, next.Mqtt.BrokerPort);
        AddIfChanged(paths, "MqttVision:Mqtt:UserName", previous.Mqtt.UserName, next.Mqtt.UserName);
        AddIfChanged(paths, "MqttVision:Mqtt:Password", previous.Mqtt.Password, next.Mqtt.Password);
        AddIfChanged(paths, "MqttVision:Mqtt:ClientId", previous.Mqtt.ClientId, next.Mqtt.ClientId);
        AddIfChanged(paths, "MqttVision:Mqtt:TaskSubmitTopic", previous.Mqtt.TaskSubmitTopic, next.Mqtt.TaskSubmitTopic);
        AddIfChanged(paths, "MqttVision:Mqtt:TaskProgressTopicTemplate", previous.Mqtt.TaskProgressTopicTemplate, next.Mqtt.TaskProgressTopicTemplate);
        AddIfChanged(paths, "MqttVision:Mqtt:TaskResultTopicTemplate", previous.Mqtt.TaskResultTopicTemplate, next.Mqtt.TaskResultTopicTemplate);
        AddIfChanged(paths, "MqttVision:Processing:EnablePlaceholderPipeline", previous.Processing.EnablePlaceholderPipeline, next.Processing.EnablePlaceholderPipeline);
        AddIfChanged(paths, "MqttVision:Processing:YoloOnnxModelPath", previous.Processing.YoloOnnxModelPath, next.Processing.YoloOnnxModelPath);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrModelDirectory", previous.Processing.PaddleOcrModelDirectory, next.Processing.PaddleOcrModelDirectory);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrEnabled", previous.Processing.PaddleOcrEnabled, next.Processing.PaddleOcrEnabled);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrDeploymentMode", previous.Processing.PaddleOcrDeploymentMode, next.Processing.PaddleOcrDeploymentMode);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrServiceUrl", previous.Processing.PaddleOcrServiceUrl, next.Processing.PaddleOcrServiceUrl);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrVisualize", previous.Processing.PaddleOcrVisualize, next.Processing.PaddleOcrVisualize);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrFileType", previous.Processing.PaddleOcrFileType, next.Processing.PaddleOcrFileType);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrUseDocOrientationClassify", previous.Processing.PaddleOcrUseDocOrientationClassify, next.Processing.PaddleOcrUseDocOrientationClassify);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrUseDocUnwarping", previous.Processing.PaddleOcrUseDocUnwarping, next.Processing.PaddleOcrUseDocUnwarping);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrUseTextlineOrientation", previous.Processing.PaddleOcrUseTextlineOrientation, next.Processing.PaddleOcrUseTextlineOrientation);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrCommand", previous.Processing.PaddleOcrCommand, next.Processing.PaddleOcrCommand);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrArgumentsTemplate", previous.Processing.PaddleOcrArgumentsTemplate, next.Processing.PaddleOcrArgumentsTemplate);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrWorkingDirectory", previous.Processing.PaddleOcrWorkingDirectory, next.Processing.PaddleOcrWorkingDirectory);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrAdditionalPath", previous.Processing.PaddleOcrAdditionalPath, next.Processing.PaddleOcrAdditionalPath);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrMinimumTextScore", previous.Processing.PaddleOcrMinimumTextScore, next.Processing.PaddleOcrMinimumTextScore);
        AddIfChanged(paths, "MqttVision:Processing:PaddleOcrTimeoutSeconds", previous.Processing.PaddleOcrTimeoutSeconds, next.Processing.PaddleOcrTimeoutSeconds);
        AddIfChanged(paths, "MqttVision:Processing:CabinetConfigurationRoot", previous.Processing.CabinetConfigurationRoot, next.Processing.CabinetConfigurationRoot);
        AddIfChanged(paths, "MqttVision:Processing:PairMaxDistancePixels", previous.Processing.PairMaxDistancePixels, next.Processing.PairMaxDistancePixels);
        AddIfChanged(paths, "MqttVision:Processing:AmbiguousDistanceTolerancePixels", previous.Processing.AmbiguousDistanceTolerancePixels, next.Processing.AmbiguousDistanceTolerancePixels);
        AddIfChanged(paths, "MqttVision:Processing:PairMaxHorizontalDistancePixels", previous.Processing.PairMaxHorizontalDistancePixels, next.Processing.PairMaxHorizontalDistancePixels);
        AddIfChanged(paths, "MqttVision:Processing:PairMaxVerticalGapPixels", previous.Processing.PairMaxVerticalGapPixels, next.Processing.PairMaxVerticalGapPixels);
        AddIfChanged(paths, "MqttVision:Processing:YoloInputSize", previous.Processing.YoloInputSize, next.Processing.YoloInputSize);
        AddIfChanged(paths, "MqttVision:Processing:ConfidenceThreshold", previous.Processing.ConfidenceThreshold, next.Processing.ConfidenceThreshold);
        AddIfChanged(paths, "MqttVision:Processing:NmsThreshold", previous.Processing.NmsThreshold, next.Processing.NmsThreshold);
        return paths;
    }

    private static void AddIfChanged<T>(
        ICollection<string> paths,
        string path,
        T previous,
        T next)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, next))
        {
            paths.Add(path);
        }
    }

    public void Dispose()
    {
        saveLock.Dispose();
    }
}
