using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class AdminMaintenanceServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_lists_maintenance_stores_without_uploads()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);
        services.Paths.EnsureDirectories();

        var snapshot = await services.Maintenance.GetSnapshotAsync();

        snapshot.Stores.Select(store => store.Name)
            .Should().BeEquivalentTo("配置备份", "操作审计", "运行日志", "检测归档");
        snapshot.Stores.Select(store => store.Path)
            .Should().NotContain(services.Paths.UploadsRoot);
    }

    [Fact]
    public async Task CleanupAsync_dry_run_reports_old_files_without_deleting()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);
        var oldBackup = await WriteOldFileAsync(services.Backups.BackupRoot, "backup-old.json", daysAgo: 45);
        var oldAudit = await WriteOldFileAsync(services.Audit.AuditRoot, "admin-audit.jsonl", daysAgo: 45);
        var oldLog = await WriteOldFileAsync(services.Paths.LogsRoot, Path.Combine("2026", "old.log"), daysAgo: 45);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = 30,
            IncludeBackups = true,
            IncludeAuditLogs = true,
            IncludeRuntimeLogs = true,
            DryRun = true
        });

        result.Success.Should().BeTrue();
        result.DryRun.Should().BeTrue();
        result.Candidates.Select(candidate => candidate.Path)
            .Should().BeEquivalentTo(oldBackup, oldAudit, oldLog);
        File.Exists(oldBackup).Should().BeTrue();
        File.Exists(oldAudit).Should().BeTrue();
        File.Exists(oldLog).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAsync_deletes_old_selected_files_and_keeps_new_files()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);
        var oldBackup = await WriteOldFileAsync(services.Backups.BackupRoot, "backup-old.json", daysAgo: 45);
        var newBackup = await WriteOldFileAsync(services.Backups.BackupRoot, "backup-new.json", daysAgo: 1);
        var oldAudit = await WriteOldFileAsync(services.Audit.AuditRoot, "admin-audit.jsonl", daysAgo: 45);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = 30,
            IncludeBackups = true,
            IncludeAuditLogs = true,
            IncludeRuntimeLogs = false,
            DryRun = false
        });

        result.Success.Should().BeTrue();
        result.DeletedPaths.Should().BeEquivalentTo(oldBackup, oldAudit);
        File.Exists(oldBackup).Should().BeFalse();
        File.Exists(oldAudit).Should().BeFalse();
        File.Exists(newBackup).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAsync_does_not_clean_archive_or_uploads_by_default()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);
        var archiveFile = await WriteOldFileAsync(
            Path.Combine(services.Paths.ArchiveRoot, "2026", "01", "01", "task-a"),
            "detection-result.json",
            daysAgo: 45);
        var uploadFile = await WriteOldFileAsync(
            Path.Combine(services.Paths.UploadsRoot, "2026", "01", "01", "task-a", "source"),
            "original.jpg",
            daysAgo: 45);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = 30,
            IncludeBackups = true,
            IncludeAuditLogs = true,
            IncludeRuntimeLogs = true,
            IncludeArchiveResults = false,
            DryRun = false
        });

        result.Candidates.Should().NotContain(candidate =>
            candidate.Path.Contains(services.Paths.ArchiveRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.Path.Contains(services.Paths.UploadsRoot, StringComparison.OrdinalIgnoreCase));
        File.Exists(archiveFile).Should().BeTrue();
        File.Exists(uploadFile).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAsync_cleans_archive_day_directories_only_when_selected()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);
        var oldArchiveDay = Path.Combine(services.Paths.ArchiveRoot, "2025", "01", "02");
        var oldArchiveFile = await WriteOldFileAsync(
            Path.Combine(oldArchiveDay, "task-a", "reports"),
            "detection-report.md",
            daysAgo: 1);
        var newArchiveDay = Path.Combine(services.Paths.ArchiveRoot, "2999", "01", "02");
        var newArchiveFile = await WriteOldFileAsync(
            Path.Combine(newArchiveDay, "task-b", "reports"),
            "detection-report.md",
            daysAgo: 45);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = 30,
            IncludeBackups = false,
            IncludeAuditLogs = false,
            IncludeRuntimeLogs = false,
            IncludeArchiveResults = true,
            DryRun = false
        });

        result.Success.Should().BeTrue();
        result.DeletedPaths.Should().Contain(oldArchiveDay);
        result.DeletedPaths.Should().NotContain(newArchiveDay);
        File.Exists(oldArchiveFile).Should().BeFalse();
        Directory.Exists(oldArchiveDay).Should().BeFalse();
        File.Exists(newArchiveFile).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task CleanupAsync_rejects_invalid_retention_days(int retentionDays)
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = retentionDays,
            IncludeBackups = true
        });

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Path == "retentionDays");
    }

    [Fact]
    public async Task CleanupAsync_rejects_empty_scope()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path);

        var result = await services.Maintenance.CleanupAsync(new AdminMaintenanceCleanupRequest
        {
            RetentionDays = 30,
            IncludeBackups = false,
            IncludeAuditLogs = false,
            IncludeRuntimeLogs = false,
            IncludeArchiveResults = false
        });

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Path == "scope");
    }

    private static TestServices CreateServices(string contentRoot)
    {
        var options = new MqttVisionServerOptions
        {
            PublicBaseUrl = "http://localhost:5080",
            StorageRoot = "runtime",
            Processing = new ProcessingOptions
            {
                CabinetConfigurationRoot = "Configuration"
            }
        };
        var environment = new TestHostEnvironment(contentRoot);
        var runtime = new RuntimeConfigurationService(
            Options.Create(options),
            environment,
            NullLogger<RuntimeConfigurationService>.Instance);
        var cabinets = new CabinetConfigurationAdminService(runtime, environment);
        var paths = new ServerPathInitializer(Options.Create(options), environment);
        var backups = new AdminConfigurationBackupService(
            runtime,
            cabinets,
            paths,
            environment,
            NullLogger<AdminConfigurationBackupService>.Instance);
        var audit = new AdminAuditService(paths, environment);
        var maintenance = new AdminMaintenanceService(paths, backups, audit);
        return new TestServices(paths, backups, audit, maintenance);
    }

    private static async Task<string> WriteOldFileAsync(
        string directory,
        string fileName,
        int daysAgo)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var targetDirectory = Path.GetDirectoryName(path) ?? directory;
        Directory.CreateDirectory(targetDirectory);
        await File.WriteAllTextAsync(path, "test");
        var timestamp = DateTime.Now.AddDays(-daysAgo);
        File.SetLastWriteTime(path, timestamp);
        Directory.SetLastWriteTime(targetDirectory, timestamp);
        return path;
    }

    private sealed record TestServices(
        ServerPathInitializer Paths,
        AdminConfigurationBackupService Backups,
        AdminAuditService Audit,
        AdminMaintenanceService Maintenance);

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-maintenance-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            var configRoot = System.IO.Path.Combine(Path, "config");
            Directory.CreateDirectory(configRoot);
            File.WriteAllText(System.IO.Path.Combine(configRoot, "mqttvision.yaml"), "MqttVision: {}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "MqttVision.Server.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
