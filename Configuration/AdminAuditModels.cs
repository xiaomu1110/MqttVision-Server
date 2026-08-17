namespace MqttVision.Server.Configuration;

public static class AdminAuditCategories
{
    public const string Authentication = "认证";

    public const string RuntimeConfiguration = "系统配置";

    public const string CabinetConfiguration = "柜体配置";

    public const string CadImport = "CAD 导入";

    public const string Backup = "备份恢复";
}

public static class AdminAuditOutcomes
{
    public const string Success = "成功";

    public const string Failure = "失败";
}

public sealed class AdminAuditWriteRequest
{
    public string Category { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Target { get; set; }

    public string? Actor { get; set; }

    public string? RemoteAddress { get; set; }

    public string? UserAgent { get; set; }

    public Dictionary<string, string?> Details { get; set; } = new();
}

public sealed record AdminAuditEntry(
    string Id,
    DateTimeOffset CreatedAt,
    string Category,
    string Action,
    string Outcome,
    string Message,
    string Target,
    string Actor,
    string RemoteAddress,
    string UserAgent,
    IReadOnlyDictionary<string, string> Details);

public sealed class AdminAuditQuery
{
    public string? Category { get; set; }

    public string? Outcome { get; set; }

    public string? Action { get; set; }

    public int Limit { get; set; } = 100;
}

public sealed record AdminAuditSnapshot(
    string AuditRoot,
    string AuditFilePath,
    IReadOnlyList<AdminAuditEntry> Entries);
