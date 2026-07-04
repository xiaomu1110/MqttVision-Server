namespace MqttVision.Server.Domain;

public sealed record DetectionTaskWorkspace(
    string TaskId,
    string RootPath,
    string MetadataRoot,
    string ReportsRoot,
    string CropsRoot,
    string TerminalCropsRoot,
    string WireTagCropsRoot,
    string CacheRoot,
    string VisualsRoot)
{
    public string ResultJsonPath => Path.Combine(ReportsRoot, "detection-result.json");

    public string OcrResultJsonPath => Path.Combine(ReportsRoot, "ocr-result.json");

    public string ConfigurationComparisonJsonPath => Path.Combine(ReportsRoot, "configuration-comparison.json");

    public string MarkdownReportPath => Path.Combine(ReportsRoot, "detection-report.md");
}
