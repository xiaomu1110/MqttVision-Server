using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MqttVision.Server.Configuration;

public sealed class AdminAuditService : IDisposable
{
    private const string AuditFileName = "admin-audit.jsonl";
    private const int MaximumDetails = 24;
    private const int MaximumQueryLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ServerPathInitializer pathInitializer;
    private readonly IHostEnvironment environment;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public AdminAuditService(
        ServerPathInitializer pathInitializer,
        IHostEnvironment environment)
    {
        this.pathInitializer = pathInitializer;
        this.environment = environment;
        AuditRoot = ResolveAuditRoot();
        AuditFilePath = Path.Combine(AuditRoot, AuditFileName);
    }

    public string AuditRoot { get; }

    public string AuditFilePath { get; }

    public Task RecordHttpAsync(
        HttpContext context,
        string category,
        string action,
        string outcome,
        string message,
        string? target = null,
        IReadOnlyDictionary<string, string?>? details = null,
        CancellationToken cancellationToken = default)
    {
        var entryDetails = ToMutableDetails(details);
        AddDetailIfMissing(entryDetails, "来源", "接口");
        AddDetailIfMissing(entryDetails, "请求路径", context.Request.Path.Value);
        AddDetailIfMissing(entryDetails, "请求方法", context.Request.Method);
        AddDetailIfMissing(entryDetails, "跟踪编号", context.TraceIdentifier);
        return RecordAsync(new AdminAuditWriteRequest
        {
            Category = category,
            Action = action,
            Outcome = outcome,
            Message = message,
            Target = target,
            Actor = context.User.Identity?.Name,
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.FirstOrDefault(),
            Details = entryDetails
        }, cancellationToken);
    }

    public Task RecordAsync(
        string category,
        string action,
        string outcome,
        string message,
        string? target = null,
        IReadOnlyDictionary<string, string?>? details = null,
        CancellationToken cancellationToken = default)
    {
        var entryDetails = ToMutableDetails(details);
        AddDetailIfMissing(entryDetails, "来源", "页面");
        return RecordAsync(new AdminAuditWriteRequest
        {
            Category = category,
            Action = action,
            Outcome = outcome,
            Message = message,
            Target = target,
            Actor = "管理员",
            Details = entryDetails
        }, cancellationToken);
    }

    public async Task RecordAsync(
        AdminAuditWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var entry = BuildEntry(request);
        Directory.CreateDirectory(AuditRoot);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(AuditFilePath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<AdminAuditSnapshot> GetSnapshotAsync(
        AdminAuditQuery? query = null,
        CancellationToken cancellationToken = default) =>
        new(
            AuditRoot,
            AuditFilePath,
            await ListAsync(query ?? new AdminAuditQuery(), cancellationToken));

    public async Task<IReadOnlyList<AdminAuditEntry>> ListAsync(
        AdminAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AuditFilePath))
        {
            return [];
        }

        var limit = Math.Clamp(query.Limit, 1, MaximumQueryLimit);
        var lines = await File.ReadAllLinesAsync(AuditFilePath, cancellationToken);
        var entries = new List<AdminAuditEntry>();
        for (var index = lines.Length - 1; index >= 0 && entries.Count < limit; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            AdminAuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AdminAuditEntry>(lines[index], JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null && Matches(entry, query))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static AdminAuditEntry BuildEntry(AdminAuditWriteRequest request) =>
        new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            NormalizeRequired(request.Category, "未分类", 32),
            NormalizeRequired(request.Action, "未知操作", 48),
            NormalizeRequired(request.Outcome, "未知", 16),
            NormalizeRequired(request.Message, "未填写说明", 240),
            NormalizeOptional(request.Target, 120),
            NormalizeOptional(request.Actor, 64, "管理员"),
            NormalizeOptional(request.RemoteAddress, 64),
            NormalizeOptional(request.UserAgent, 240),
            NormalizeDetails(request.Details));

    private static bool Matches(AdminAuditEntry entry, AdminAuditQuery query) =>
        MatchesFilter(entry.Category, query.Category) &&
        MatchesFilter(entry.Outcome, query.Outcome) &&
        MatchesFilter(entry.Action, query.Action);

    private static bool MatchesFilter(string value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string?> ToMutableDetails(
        IReadOnlyDictionary<string, string?>? details) =>
        details is null
            ? new Dictionary<string, string?>()
            : details.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static void AddDetailIfMissing(
        IDictionary<string, string?> details,
        string key,
        string? value)
    {
        if (!details.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
        {
            details.Add(key, value);
        }
    }

    private static IReadOnlyDictionary<string, string> NormalizeDetails(
        IReadOnlyDictionary<string, string?> details) =>
        details
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Take(MaximumDetails)
            .ToDictionary(
                pair => NormalizeRequired(pair.Key, "字段", 64),
                pair => NormalizeRequired(pair.Value, "空", 300),
                StringComparer.OrdinalIgnoreCase);

    private static string NormalizeRequired(string? value, string fallback, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static string NormalizeOptional(
        string? value,
        int maximumLength,
        string fallback = "") =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : NormalizeRequired(value, fallback, maximumLength);

    private string ResolveAuditRoot()
    {
        var defaultRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, ".admin-audit"));
        var storageRoot = Path.GetFullPath(pathInitializer.StorageRoot);
        return IsSameOrChildPath(defaultRoot, storageRoot)
            ? Path.Combine(GetApplicationDataRoot(), "MqttVision.Server", HashPath(environment.ContentRootPath), "admin-audit")
            : defaultRoot;
    }

    private static string GetApplicationDataRoot()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return localApplicationData;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Path.Combine(Path.GetTempPath(), "MqttVision")
            : Path.Combine(userProfile, ".mqttvision");
    }

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        writeLock.Dispose();
    }
}
