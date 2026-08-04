using System.Globalization;
using System.Text.Json.Serialization;

namespace MqttVision.Server.Configuration;

public enum ConfigurationApplyMode
{
    HotReload,
    RequiresReconnect,
    RequiresModelReload,
    RequiresRestart
}

public sealed record ConfigurationFieldDescriptor(
    string Path,
    string Label,
    string Section,
    ConfigurationApplyMode ApplyMode,
    string Description);

public sealed record ConfigurationValidationIssue(
    string Path,
    string Message,
    bool IsWarning = false);

public sealed record RuntimeConfigurationValidationResult(
    IReadOnlyList<ConfigurationValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.IsWarning);

    public IReadOnlyList<ConfigurationValidationIssue> Errors =>
        Issues.Where(issue => !issue.IsWarning).ToArray();

    public IReadOnlyList<ConfigurationValidationIssue> Warnings =>
        Issues.Where(issue => issue.IsWarning).ToArray();

    public static RuntimeConfigurationValidationResult Valid { get; } = new([]);
}

public sealed record RuntimeConfigurationSnapshot(
    long Version,
    DateTimeOffset UpdatedAt,
    string LocalConfigPath,
    MqttVisionServerOptions Options,
    RuntimeConfigurationValidationResult Validation,
    IReadOnlyList<ConfigurationFieldDescriptor> Fields);

public sealed record RuntimeConfigurationSaveResult(
    bool Success,
    string Message,
    RuntimeConfigurationSnapshot Snapshot,
    IReadOnlyList<string> ChangedPaths);

public sealed class RuntimeConfigurationChangedEventArgs(
    MqttVisionServerOptions previous,
    MqttVisionServerOptions current,
    IReadOnlyList<string> changedPaths,
    long version) : EventArgs
{
    public MqttVisionServerOptions Previous { get; } = previous;

    public MqttVisionServerOptions Current { get; } = current;

    public IReadOnlyList<string> ChangedPaths { get; } = changedPaths;

    public long Version { get; } = version;
}

public sealed class AdminConfigurationForm
{
    public string PublicBaseUrl { get; set; } = string.Empty;

    public string StorageRoot { get; set; } = string.Empty;

    public long MaxUploadBytes { get; set; }

    public AdminMqttConfigurationForm Mqtt { get; set; } = new();

    public AdminProcessingConfigurationForm Processing { get; set; } = new();

    public static AdminConfigurationForm FromOptions(MqttVisionServerOptions options) =>
        new()
        {
            PublicBaseUrl = options.PublicBaseUrl,
            StorageRoot = options.StorageRoot,
            MaxUploadBytes = options.MaxUploadBytes,
            Mqtt = AdminMqttConfigurationForm.FromOptions(options.Mqtt),
            Processing = AdminProcessingConfigurationForm.FromOptions(options.Processing)
        };

    public MqttVisionServerOptions ToOptions() =>
        new()
        {
            PublicBaseUrl = PublicBaseUrl.Trim(),
            StorageRoot = StorageRoot.Trim(),
            MaxUploadBytes = MaxUploadBytes,
            Mqtt = Mqtt.ToOptions(),
            Processing = Processing.ToOptions()
        };
}

public sealed class AdminMqttConfigurationForm
{
    public string BrokerHost { get; set; } = string.Empty;

    public int BrokerPort { get; set; }

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string TaskSubmitTopic { get; set; } = string.Empty;

    public string TaskProgressTopicTemplate { get; set; } = string.Empty;

    public string TaskResultTopicTemplate { get; set; } = string.Empty;

    public static AdminMqttConfigurationForm FromOptions(MqttOptions options) =>
        new()
        {
            BrokerHost = options.BrokerHost,
            BrokerPort = options.BrokerPort,
            UserName = options.UserName,
            Password = options.Password,
            ClientId = options.ClientId,
            TaskSubmitTopic = options.TaskSubmitTopic,
            TaskProgressTopicTemplate = options.TaskProgressTopicTemplate,
            TaskResultTopicTemplate = options.TaskResultTopicTemplate
        };

    public MqttOptions ToOptions() =>
        new()
        {
            BrokerHost = BrokerHost.Trim(),
            BrokerPort = BrokerPort,
            UserName = NormalizeOptional(UserName),
            Password = NormalizeOptional(Password),
            ClientId = ClientId.Trim(),
            TaskSubmitTopic = TaskSubmitTopic.Trim(),
            TaskProgressTopicTemplate = TaskProgressTopicTemplate.Trim(),
            TaskResultTopicTemplate = TaskResultTopicTemplate.Trim()
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class AdminProcessingConfigurationForm
{
    public bool EnablePlaceholderPipeline { get; set; }

    public string YoloOnnxModelPath { get; set; } = string.Empty;

    public string PaddleOcrModelDirectory { get; set; } = string.Empty;

    public bool PaddleOcrEnabled { get; set; }

    public string PaddleOcrDeploymentMode { get; set; } = string.Empty;

    public string PaddleOcrServiceUrl { get; set; } = string.Empty;

    public bool PaddleOcrVisualize { get; set; }

    public int PaddleOcrFileType { get; set; }

    public bool? PaddleOcrUseDocOrientationClassify { get; set; }

    public bool? PaddleOcrUseDocUnwarping { get; set; }

    public bool? PaddleOcrUseTextlineOrientation { get; set; }

    public string PaddleOcrCommand { get; set; } = string.Empty;

    public string PaddleOcrArgumentsTemplate { get; set; } = string.Empty;

    public string PaddleOcrWorkingDirectory { get; set; } = string.Empty;

    public string PaddleOcrAdditionalPath { get; set; } = string.Empty;

    public double PaddleOcrMinimumTextScore { get; set; }

    public int PaddleOcrTimeoutSeconds { get; set; }

    public string CabinetConfigurationRoot { get; set; } = string.Empty;

    public double PairMaxDistancePixels { get; set; }

    public double AmbiguousDistanceTolerancePixels { get; set; }

    public double PairMaxHorizontalDistancePixels { get; set; }

    public double PairMaxVerticalGapPixels { get; set; }

    public int YoloInputSize { get; set; }

    public float ConfidenceThreshold { get; set; }

    public float NmsThreshold { get; set; }

    public static AdminProcessingConfigurationForm FromOptions(ProcessingOptions options) =>
        new()
        {
            EnablePlaceholderPipeline = options.EnablePlaceholderPipeline,
            YoloOnnxModelPath = options.YoloOnnxModelPath,
            PaddleOcrModelDirectory = options.PaddleOcrModelDirectory,
            PaddleOcrEnabled = options.PaddleOcrEnabled,
            PaddleOcrDeploymentMode = options.PaddleOcrDeploymentMode,
            PaddleOcrServiceUrl = options.PaddleOcrServiceUrl,
            PaddleOcrVisualize = options.PaddleOcrVisualize,
            PaddleOcrFileType = options.PaddleOcrFileType,
            PaddleOcrUseDocOrientationClassify = options.PaddleOcrUseDocOrientationClassify,
            PaddleOcrUseDocUnwarping = options.PaddleOcrUseDocUnwarping,
            PaddleOcrUseTextlineOrientation = options.PaddleOcrUseTextlineOrientation,
            PaddleOcrCommand = options.PaddleOcrCommand,
            PaddleOcrArgumentsTemplate = options.PaddleOcrArgumentsTemplate,
            PaddleOcrWorkingDirectory = options.PaddleOcrWorkingDirectory,
            PaddleOcrAdditionalPath = options.PaddleOcrAdditionalPath,
            PaddleOcrMinimumTextScore = options.PaddleOcrMinimumTextScore,
            PaddleOcrTimeoutSeconds = options.PaddleOcrTimeoutSeconds,
            CabinetConfigurationRoot = options.CabinetConfigurationRoot,
            PairMaxDistancePixels = options.PairMaxDistancePixels,
            AmbiguousDistanceTolerancePixels = options.AmbiguousDistanceTolerancePixels,
            PairMaxHorizontalDistancePixels = options.PairMaxHorizontalDistancePixels,
            PairMaxVerticalGapPixels = options.PairMaxVerticalGapPixels,
            YoloInputSize = options.YoloInputSize,
            ConfidenceThreshold = options.ConfidenceThreshold,
            NmsThreshold = options.NmsThreshold
        };

    public ProcessingOptions ToOptions() =>
        new()
        {
            EnablePlaceholderPipeline = EnablePlaceholderPipeline,
            YoloOnnxModelPath = YoloOnnxModelPath.Trim(),
            PaddleOcrModelDirectory = PaddleOcrModelDirectory.Trim(),
            PaddleOcrEnabled = PaddleOcrEnabled,
            PaddleOcrDeploymentMode = PaddleOcrDeploymentMode.Trim(),
            PaddleOcrServiceUrl = PaddleOcrServiceUrl.Trim(),
            PaddleOcrVisualize = PaddleOcrVisualize,
            PaddleOcrFileType = PaddleOcrFileType,
            PaddleOcrUseDocOrientationClassify = PaddleOcrUseDocOrientationClassify,
            PaddleOcrUseDocUnwarping = PaddleOcrUseDocUnwarping,
            PaddleOcrUseTextlineOrientation = PaddleOcrUseTextlineOrientation,
            PaddleOcrCommand = PaddleOcrCommand.Trim(),
            PaddleOcrArgumentsTemplate = PaddleOcrArgumentsTemplate.Trim(),
            PaddleOcrWorkingDirectory = PaddleOcrWorkingDirectory.Trim(),
            PaddleOcrAdditionalPath = PaddleOcrAdditionalPath.Trim(),
            PaddleOcrMinimumTextScore = PaddleOcrMinimumTextScore,
            PaddleOcrTimeoutSeconds = PaddleOcrTimeoutSeconds,
            CabinetConfigurationRoot = CabinetConfigurationRoot.Trim(),
            PairMaxDistancePixels = PairMaxDistancePixels,
            AmbiguousDistanceTolerancePixels = AmbiguousDistanceTolerancePixels,
            PairMaxHorizontalDistancePixels = PairMaxHorizontalDistancePixels,
            PairMaxVerticalGapPixels = PairMaxVerticalGapPixels,
            YoloInputSize = YoloInputSize,
            ConfidenceThreshold = ConfidenceThreshold,
            NmsThreshold = NmsThreshold
        };

    [JsonIgnore]
    public string PaddleOcrMinimumTextScoreText
    {
        get => PaddleOcrMinimumTextScore.ToString("0.###", CultureInfo.InvariantCulture);
        set => PaddleOcrMinimumTextScore = ParseDouble(value, PaddleOcrMinimumTextScore);
    }

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
