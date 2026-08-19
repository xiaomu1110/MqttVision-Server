using System.Text.Json.Serialization;

namespace MqttVision.Server.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationImportBatchStatus>))]
public enum ConfigurationImportBatchStatus
{
    Queued,
    Processing,
    Completed,
    CompletedWithErrors,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationImportFileStatus>))]
public enum ConfigurationImportFileStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public sealed class ConfigurationImportBatchRecord
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ConfigurationImportBatchStatus Status { get; set; } = ConfigurationImportBatchStatus.Queued;

    public string RootPath { get; init; } = string.Empty;

    public string StatePath { get; init; } = string.Empty;

    public List<ConfigurationImportFileRecord> Files { get; init; } = new();

    [JsonIgnore]
    public int TotalFiles => Files.Count;

    [JsonIgnore]
    public int CompletedFiles => Files.Count(file => file.Status == ConfigurationImportFileStatus.Completed);

    [JsonIgnore]
    public int FailedFiles => Files.Count(file => file.Status == ConfigurationImportFileStatus.Failed);

    [JsonIgnore]
    public int ProcessingFiles => Files.Count(file => file.Status == ConfigurationImportFileStatus.Processing);
}

public sealed class ConfigurationImportFileRecord
{
    public string FileId { get; init; } = string.Empty;

    public string OriginalFileName { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public string? ContentType { get; init; }

    public string Extension { get; init; } = string.Empty;

    public ConfigurationImportFileStatus Status { get; set; } = ConfigurationImportFileStatus.Queued;

    public int ProgressPercent { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string SourcePath { get; init; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public string BackupPath { get; set; } = string.Empty;

    public string? PreviousConfigPath { get; set; }

    public string? SourceUrl { get; set; }

    public string? ConfigUrl { get; set; }

    public string? BackupUrl { get; set; }

    public string? Sha256 { get; set; }

    public int TerminalStripCount { get; set; }

    public int TerminalCount { get; set; }

    public List<string> Warnings { get; init; } = new();

    public string? ErrorMessage { get; set; }

    public List<ConfigurationImportTerminalPreview> PreviewRows { get; init; } = new();
}

public sealed class ConfigurationImportTerminalPreview
{
    public string StripCode { get; init; } = string.Empty;

    public string TerminalLabel { get; init; } = string.Empty;

    public List<string> WireMarkers { get; init; } = new();

    public bool IsExpectedEmpty { get; init; }
}

public sealed record ConfigurationImportUpload(
    string FileName,
    long Length,
    string? ContentType,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);
