using System.Text.Json.Serialization;

namespace MqttVision.Server.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<CadImportBatchStatus>))]
public enum CadImportBatchStatus
{
    Queued,
    Processing,
    Completed,
    CompletedWithErrors,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<CadImportFileStatus>))]
public enum CadImportFileStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public sealed class CadImportBatchRecord
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public CadImportBatchStatus Status { get; set; } = CadImportBatchStatus.Queued;

    public string RootPath { get; init; } = string.Empty;

    public string StatePath { get; init; } = string.Empty;

    public List<CadImportFileRecord> Files { get; init; } = new();

    [JsonIgnore]
    public int TotalFiles => Files.Count;

    [JsonIgnore]
    public int CompletedFiles => Files.Count(file => file.Status == CadImportFileStatus.Completed);

    [JsonIgnore]
    public int FailedFiles => Files.Count(file => file.Status == CadImportFileStatus.Failed);

    [JsonIgnore]
    public int ProcessingFiles => Files.Count(file => file.Status == CadImportFileStatus.Processing);
}

public sealed class CadImportFileRecord
{
    public string FileId { get; init; } = string.Empty;

    public string OriginalFileName { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public string? ContentType { get; init; }

    public string Extension { get; init; } = string.Empty;

    public CadImportFileStatus Status { get; set; } = CadImportFileStatus.Queued;

    public int ProgressPercent { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string SourcePath { get; init; } = string.Empty;

    public string RawTextPath { get; set; } = string.Empty;

    public string RelationsPath { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public string BackupPath { get; set; } = string.Empty;

    public string? PreviousConfigPath { get; set; }

    public string? SourceUrl { get; set; }

    public string? RawTextUrl { get; set; }

    public string? RelationsUrl { get; set; }

    public string? ConfigUrl { get; set; }

    public string? BackupUrl { get; set; }

    public string? Sha256 { get; set; }

    public int ExtractedTextCount { get; set; }

    public int TerminalStripCount { get; set; }

    public int TerminalCount { get; set; }

    public List<string> Warnings { get; init; } = new();

    public string? ErrorMessage { get; set; }

    public List<CadImportTerminalPreview> PreviewRows { get; init; } = new();
}

public sealed class CadImportTerminalPreview
{
    public string StripCode { get; init; } = string.Empty;

    public string Orientation { get; init; } = string.Empty;

    public string TerminalLabel { get; init; } = string.Empty;

    public string? LeftWireMarker { get; init; }

    public string? RightWireMarker { get; init; }

    public string? AuxiliaryValue { get; init; }

    public string? Destination { get; init; }

    public bool IsExpectedEmpty { get; init; }
}

public sealed record CadImportUpload(
    string FileName,
    long Length,
    string? ContentType,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);
