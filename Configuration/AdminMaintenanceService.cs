namespace MqttVision.Server.Configuration;

public sealed class AdminMaintenanceService
{
    private readonly ServerPathInitializer paths;
    private readonly AdminConfigurationBackupService backups;
    private readonly AdminAuditService audit;

    public AdminMaintenanceService(
        ServerPathInitializer paths,
        AdminConfigurationBackupService backups,
        AdminAuditService audit)
    {
        this.paths = paths;
        this.backups = backups;
        this.audit = audit;
    }

    public Task<AdminMaintenanceSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdminMaintenanceSnapshot(
            DateTimeOffset.Now,
            BuildStores(cancellationToken),
            new AdminMaintenanceCleanupRequest()));

    public Task<AdminMaintenanceCleanupResult> CleanupAsync(
        AdminMaintenanceCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var issues = Validate(request).ToList();
        if (issues.Any(issue => !issue.IsWarning))
        {
            return Task.FromResult(new AdminMaintenanceCleanupResult(
                false,
                "清理参数校验未通过，未执行。",
                request.DryRun,
                request.RetentionDays,
                DateTimeOffset.Now,
                [],
                [],
                issues));
        }

        var cutoff = DateTimeOffset.Now.AddDays(-request.RetentionDays);
        var stores = SelectStores(request);
        var candidates = new List<AdminMaintenanceCleanupCandidate>();
        foreach (var store in stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(FindCandidates(store, cutoff, cancellationToken));
        }

        var deletedPaths = new List<string>();
        if (!request.DryRun)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    DeleteCandidate(candidate);
                    deletedPaths.Add(candidate.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ConfigurationValidationIssue(
                        candidate.Path,
                        $"无法删除：{ex.Message}",
                        true));
                }
            }
        }

        var message = request.DryRun
            ? $"预览完成，找到 {candidates.Count} 个可清理项。"
            : $"清理完成，已删除 {deletedPaths.Count} 个项目。";
        return Task.FromResult(new AdminMaintenanceCleanupResult(
            true,
            message,
            request.DryRun,
            request.RetentionDays,
            cutoff,
            candidates,
            deletedPaths,
            issues));
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024)
        {
            return $"{kilobytes:0.#} KB";
        }

        var megabytes = kilobytes / 1024d;
        if (megabytes < 1024)
        {
            return $"{megabytes:0.##} MB";
        }

        return $"{megabytes / 1024d:0.##} GB";
    }

    private IReadOnlyList<AdminMaintenanceStoreSummary> BuildStores(CancellationToken cancellationToken) =>
        GetAllStores()
            .Select(store => BuildStoreSummary(store, cancellationToken))
            .ToArray();

    private static AdminMaintenanceStoreSummary BuildStoreSummary(
        MaintenanceStore store,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(store.Path))
        {
            return new AdminMaintenanceStoreSummary(
                store.Name,
                store.Path,
                false,
                0,
                FormatSize(0),
                0,
                null,
                null);
        }

        var files = Directory
            .EnumerateFiles(store.Path, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(path);
            })
            .Where(file => file.Exists)
            .ToArray();
        var size = files.Sum(file => file.Length);
        return new AdminMaintenanceStoreSummary(
            store.Name,
            store.Path,
            true,
            size,
            FormatSize(size),
            files.Length,
            files.Length == 0 ? null : files.Min(file => new DateTimeOffset(file.LastWriteTime)),
            files.Length == 0 ? null : files.Max(file => new DateTimeOffset(file.LastWriteTime)));
    }

    private IReadOnlyList<MaintenanceStore> GetAllStores() =>
        [
            new("配置备份", backups.BackupRoot, CleanupMode.Files),
            new("操作审计", audit.AuditRoot, CleanupMode.Files),
            new("运行日志", paths.LogsRoot, CleanupMode.Files),
            new("检测归档", paths.ArchiveRoot, CleanupMode.DayDirectories),
            new("JSON 配置导入数据", paths.ConfigurationImportsRoot, CleanupMode.DayDirectories)
        ];

    private IReadOnlyList<MaintenanceStore> SelectStores(AdminMaintenanceCleanupRequest request)
    {
        var stores = new List<MaintenanceStore>();
        if (request.IncludeBackups)
        {
            stores.Add(new MaintenanceStore("配置备份", backups.BackupRoot, CleanupMode.Files));
        }

        if (request.IncludeAuditLogs)
        {
            stores.Add(new MaintenanceStore("操作审计", audit.AuditRoot, CleanupMode.Files));
        }

        if (request.IncludeRuntimeLogs)
        {
            stores.Add(new MaintenanceStore("运行日志", paths.LogsRoot, CleanupMode.Files));
        }

        if (request.IncludeArchiveResults)
        {
            stores.Add(new MaintenanceStore("检测归档", paths.ArchiveRoot, CleanupMode.DayDirectories));
        }

        if (request.IncludeJsonImports)
        {
            stores.Add(new MaintenanceStore("JSON 配置导入数据", paths.ConfigurationImportsRoot, CleanupMode.DayDirectories));
        }

        return stores;
    }

    private IReadOnlyList<ConfigurationValidationIssue> Validate(AdminMaintenanceCleanupRequest request)
    {
        var issues = new List<ConfigurationValidationIssue>();
        if (request.RetentionDays is < 1 or > 3650)
        {
            issues.Add(new ConfigurationValidationIssue("retentionDays", "保留天数必须在 1 到 3650 之间。"));
        }

        if (!request.IncludeBackups &&
            !request.IncludeAuditLogs &&
            !request.IncludeRuntimeLogs &&
            !request.IncludeArchiveResults &&
            !request.IncludeJsonImports)
        {
            issues.Add(new ConfigurationValidationIssue("scope", "至少需要选择一个清理范围。"));
        }

        return issues;
    }

    private static IReadOnlyList<AdminMaintenanceCleanupCandidate> FindCandidates(
        MaintenanceStore store,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(store.Path))
        {
            return [];
        }

        return store.Mode switch
        {
            CleanupMode.Files => Directory
                .EnumerateFiles(store.Path, "*", SearchOption.AllDirectories)
                .Select(path => BuildFileCandidate(store.Name, store.Path, path, cutoff, cancellationToken))
                .Where(candidate => candidate is not null)
                .Cast<AdminMaintenanceCleanupCandidate>()
                .ToArray(),
            CleanupMode.DayDirectories => EnumerateDayDirectories(store.Path)
                .Select(path => BuildDirectoryCandidate(store.Name, store.Path, path, cutoff, cancellationToken))
                .Where(candidate => candidate is not null)
                .Cast<AdminMaintenanceCleanupCandidate>()
                .ToArray(),
            _ => []
        };
    }

    private static AdminMaintenanceCleanupCandidate? BuildFileCandidate(
        string storeName,
        string storeRoot,
        string path,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists || new DateTimeOffset(info.LastWriteTime) >= cutoff)
        {
            return null;
        }

        return new AdminMaintenanceCleanupCandidate(
            storeName,
            storeRoot,
            info.FullName,
            info.Length,
            FormatSize(info.Length),
            info.LastWriteTime);
    }

    private static AdminMaintenanceCleanupCandidate? BuildDirectoryCandidate(
        string storeName,
        string storeRoot,
        string path,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(path);
        if (!info.Exists || !ShouldCleanDirectory(info, cutoff))
        {
            return null;
        }

        var size = Directory
            .EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(file);
            })
            .Where(file => file.Exists)
            .Sum(file => file.Length);
        return new AdminMaintenanceCleanupCandidate(
            storeName,
            storeRoot,
            info.FullName,
            size,
            FormatSize(size),
            info.LastWriteTime);
    }

    private static IEnumerable<string> EnumerateDayDirectories(string root)
    {
        foreach (var yearPath in Directory.EnumerateDirectories(root))
        {
            foreach (var monthPath in Directory.EnumerateDirectories(yearPath))
            {
                foreach (var dayPath in Directory.EnumerateDirectories(monthPath))
                {
                    yield return dayPath;
                }
            }
        }
    }

    private static bool ShouldCleanDirectory(DirectoryInfo directory, DateTimeOffset cutoff)
    {
        if (!TryGetArchiveDay(directory, out var archiveDay))
        {
            return false;
        }

        return archiveDay < cutoff.LocalDateTime.Date;
    }

    private static bool TryGetArchiveDay(DirectoryInfo directory, out DateTime archiveDay)
    {
        archiveDay = default;
        if (directory.Parent?.Parent is null)
        {
            return false;
        }

        var text = $"{directory.Parent.Parent.Name}-{directory.Parent.Name}-{directory.Name}";
        return DateTime.TryParseExact(
            text,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out archiveDay);
    }

    private static void DeleteCandidate(AdminMaintenanceCleanupCandidate candidate)
    {
        if (!IsSameOrChildPath(candidate.Path, candidate.StoreRoot))
        {
            throw new IOException("清理路径超出允许范围。");
        }

        if (File.Exists(candidate.Path))
        {
            File.Delete(candidate.Path);
            RemoveEmptyParents(Path.GetDirectoryName(candidate.Path), candidate.StoreRoot);
            return;
        }

        if (Directory.Exists(candidate.Path))
        {
            Directory.Delete(candidate.Path, true);
            RemoveEmptyParents(Path.GetDirectoryName(candidate.Path), candidate.StoreRoot);
        }
    }

    private static void RemoveEmptyParents(string? path, string root)
    {
        while (!string.IsNullOrWhiteSpace(path) &&
            IsSameOrChildPath(path, root) &&
            !Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(path) &&
            !Directory.EnumerateFileSystemEntries(path).Any())
        {
            var parent = Directory.GetParent(path)?.FullName;
            Directory.Delete(path);
            path = parent;
        }
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private sealed record MaintenanceStore(
        string Name,
        string Path,
        CleanupMode Mode);

    private enum CleanupMode
    {
        Files,
        DayDirectories
    }
}
