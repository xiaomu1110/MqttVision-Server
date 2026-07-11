namespace MqttVision.Server.Configuration;

public sealed class AdminConfigurationBackupCreateRequest
{
    public string? Description { get; set; }

    public bool IncludeRuntime { get; set; } = true;

    public bool IncludeCabinets { get; set; } = true;
}

public sealed class AdminConfigurationBackupRestoreRequest
{
    public bool IncludeRuntime { get; set; } = true;

    public bool IncludeCabinets { get; set; } = true;

    public bool AllowWarnings { get; set; } = true;

    public bool CreateSafetyBackup { get; set; } = true;
}

public sealed record AdminConfigurationBackupSummary(
    string BackupId,
    DateTimeOffset CreatedAt,
    string Description,
    bool HasRuntimeConfiguration,
    int CabinetCount,
    long SizeBytes,
    string SizeText,
    string FilePath);

public sealed record AdminConfigurationBackupCreateResult(
    bool Success,
    string Message,
    AdminConfigurationBackupSummary? Backup,
    IReadOnlyList<ConfigurationValidationIssue> Issues);

public sealed record AdminConfigurationBackupRestorePlan(
    bool CanRestore,
    string BackupId,
    DateTimeOffset CreatedAt,
    string Description,
    bool HasRuntimeConfiguration,
    int RuntimeChangedFieldCount,
    int CabinetCount,
    IReadOnlyList<ConfigurationValidationIssue> Issues);

public sealed record AdminConfigurationBackupRestoreResult(
    bool Success,
    string Message,
    AdminConfigurationBackupRestorePlan Plan,
    AdminConfigurationBackupSummary? SafetyBackup,
    RuntimeConfigurationSaveResult? RuntimeResult,
    IReadOnlyList<CabinetConfigurationSaveResult> CabinetResults,
    IReadOnlyList<ConfigurationValidationIssue> Issues);

public sealed class AdminConfigurationBackupDocument
{
    public string SchemaVersion { get; set; } = AdminConfigurationBackupService.CurrentSchemaVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    public string Description { get; set; } = string.Empty;

    public RuntimeConfigurationBackupEntry? RuntimeConfiguration { get; set; }

    public List<CabinetConfigurationBackupEntry> Cabinets { get; set; } = new();
}

public sealed class RuntimeConfigurationBackupEntry
{
    public long Version { get; set; }

    public string LocalConfigPath { get; set; } = string.Empty;

    public AdminConfigurationForm Configuration { get; set; } = new();
}

public sealed class CabinetConfigurationBackupEntry
{
    public string CabinetId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public CabinetConfigurationEditorForm Configuration { get; set; } = new();
}
