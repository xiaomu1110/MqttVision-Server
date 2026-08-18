namespace MqttVision.Server.Configuration;

public sealed class AdminMaintenanceCleanupRequest
{
    public int RetentionDays { get; set; } = 30;

    public bool IncludeBackups { get; set; } = true;

    public bool IncludeAuditLogs { get; set; } = true;

    public bool IncludeRuntimeLogs { get; set; } = true;

    public bool IncludeArchiveResults { get; set; }

    /// <summary>
    /// 清理 CAD 导入批次中的原始图纸、提取文本、解析关系、批次状态和导入备份。
    /// 正在使用的柜体配置文件位于 Configuration 目录，不属于此清理范围。
    /// </summary>
    public bool IncludeCadImports { get; set; }

    public bool DryRun { get; set; } = true;
}

public sealed record AdminMaintenanceStoreSummary(
    string Name,
    string Path,
    bool Exists,
    long SizeBytes,
    string SizeText,
    int FileCount,
    DateTimeOffset? OldestWriteTime,
    DateTimeOffset? NewestWriteTime);

public sealed record AdminMaintenanceCleanupCandidate(
    string StoreName,
    string StoreRoot,
    string Path,
    long SizeBytes,
    string SizeText,
    DateTimeOffset LastWriteTime);

public sealed record AdminMaintenanceCleanupResult(
    bool Success,
    string Message,
    bool DryRun,
    int RetentionDays,
    DateTimeOffset CutoffTime,
    IReadOnlyList<AdminMaintenanceCleanupCandidate> Candidates,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<ConfigurationValidationIssue> Issues)
{
    public int CandidateCount => Candidates.Count;

    public int DeletedCount => DeletedPaths.Count;

    public long CandidateSizeBytes => Candidates.Sum(candidate => candidate.SizeBytes);

    public string CandidateSizeText => AdminMaintenanceService.FormatSize(CandidateSizeBytes);
}

public sealed record AdminMaintenanceSnapshot(
    DateTimeOffset ServerTime,
    IReadOnlyList<AdminMaintenanceStoreSummary> Stores,
    AdminMaintenanceCleanupRequest DefaultCleanup);
