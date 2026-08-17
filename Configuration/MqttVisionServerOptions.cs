namespace MqttVision.Server.Configuration;

public sealed class MqttVisionServerOptions
{
    public const string SectionName = "MqttVision";

    public string PublicBaseUrl { get; init; } = "http://localhost:5080";

    public string StorageRoot { get; init; } = "runtime";

    public long MaxUploadBytes { get; init; } = 25 * 1024 * 1024;

    public MqttOptions Mqtt { get; init; } = new();

    public ProcessingOptions Processing { get; init; } = new();

    public CadImportOptions CadImport { get; init; } = new();
}

public sealed class CadImportOptions
{
    public int MaxConcurrentParsers { get; init; } = 3;

    public long MaxFileBytes { get; init; } = 100 * 1024 * 1024;

    public int ParserTimeoutSeconds { get; init; } = 300;

    public string[] AllowedExtensions { get; init; } = [".dwg", ".dxf"];
}

public sealed class MqttOptions
{
    public string BrokerHost { get; init; } = "localhost";

    public int BrokerPort { get; init; } = 1883;

    public string? UserName { get; init; }

    public string? Password { get; init; }

    public string ClientId { get; init; } = "mqttvision-server";

    public string TaskSubmitTopic { get; init; } = "mqttvision/+/+/task/submit";

    public string TaskProgressTopicTemplate { get; init; } = "mqttvision/{siteId}/{deviceId}/task/progress";

    public string TaskResultTopicTemplate { get; init; } = "mqttvision/{siteId}/{deviceId}/task/result";

    public string BrokerEndpoint => $"{BrokerHost}:{BrokerPort}";
}

public sealed class ProcessingOptions
{
    public bool EnablePlaceholderPipeline { get; init; } = true;

    public string YoloOnnxModelPath { get; init; } = string.Empty;

    public string PaddleOcrModelDirectory { get; init; } = string.Empty;

    public bool PaddleOcrEnabled { get; init; }

    public string PaddleOcrDeploymentMode { get; init; } = "high-stability-serving";

    public string PaddleOcrServiceUrl { get; init; } = "http://localhost:8000/v2/models/ocr/infer";

    public bool PaddleOcrVisualize { get; init; }

    public int PaddleOcrFileType { get; init; } = 1;

    public bool? PaddleOcrUseDocOrientationClassify { get; init; } = true;

    public bool? PaddleOcrUseDocUnwarping { get; init; }

    public bool? PaddleOcrUseTextlineOrientation { get; init; } = true;

    public double PaddleOcrMinimumTextScore { get; init; } = 0.8;

    public string PaddleOcrCommand { get; init; } = string.Empty;

    public string PaddleOcrArgumentsTemplate { get; init; } = "{image}";

    public string PaddleOcrWorkingDirectory { get; init; } = string.Empty;

    public string PaddleOcrAdditionalPath { get; init; } = string.Empty;

    public int PaddleOcrTimeoutSeconds { get; init; } = 30;

    public string CabinetConfigurationRoot { get; init; } = "configuration";

    public double PairMaxDistancePixels { get; init; } = 160;

    public double AmbiguousDistanceTolerancePixels { get; init; } = 15;

    public double PairMaxHorizontalDistancePixels { get; init; } = 130;

    public double PairMaxVerticalGapPixels { get; init; } = 120;

    public int YoloInputSize { get; init; } = 1080;

    public float ConfidenceThreshold { get; init; } = 0.8f;

    public float NmsThreshold { get; init; } = 0.4f;
}
