namespace MqttVision.Server.Configuration;

public sealed class AdminMaintenanceCleanupRequest
{
    public int RetentionDays { get; set; } = 30;

    public bool IncludeBackups { get; set; } = true;

    public bool IncludeAuditLogs { get; set; } = true;

    public bool IncludeRuntimeLogs { get; set; } = true;

    public bool IncludeArchiveResults { get; set; }

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
