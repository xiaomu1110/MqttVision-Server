using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class AdminConfigurationBackupServiceTests
{
    [Fact]
    public async Task CreateAsync_writes_backup_outside_public_storage_root()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path, CreateOptions());
        await services.Cabinets.SaveAsync(new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = "001/A" }
            ]
        });

        var result = await services.Backups.CreateAsync(new AdminConfigurationBackupCreateRequest
        {
            Description = "测试备份",
            IncludeRuntime = true,
            IncludeCabinets = true
        });

        result.Success.Should().BeTrue();
        result.Backup.Should().NotBeNull();
        File.Exists(result.Backup!.FilePath).Should().BeTrue();
        result.Backup.HasRuntimeConfiguration.Should().BeTrue();
        result.Backup.CabinetCount.Should().Be(1);
        result.Backup.FilePath.Should().StartWith(Path.Combine(sandbox.Path, ".admin-backups"));
        result.Backup.FilePath.Should().NotStartWith(services.Paths.StorageRoot);

        var content = await File.ReadAllTextAsync(result.Backup.FilePath);
        content.Should().Contain("测试备份");
        content.Should().Contain("cabinet-a");
    }

    [Fact]
    public async Task RestoreAsync_restores_runtime_configuration_and_cabinets()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path, CreateOptions());
        await services.Cabinets.SaveAsync(new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = "001/A" }
            ]
        });
        var backup = await services.Backups.CreateAsync(new AdminConfigurationBackupCreateRequest());
        backup.Backup.Should().NotBeNull();

        var changedForm = AdminConfigurationForm.FromOptions(services.Runtime.Current);
        changedForm.PublicBaseUrl = "http://after.example:5080";
        var changedRuntime = await services.Runtime.SaveAsync(changedForm);
        changedRuntime.Success.Should().BeTrue();
        await services.Cabinets.SaveAsync(new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = "002/B" }
            ]
        });

        RuntimeConfigurationChangedEventArgs? changed = null;
        services.Runtime.Changed += (_, args) => changed = args;

        var result = await services.Backups.RestoreAsync(
            backup.Backup!.BackupId,
            new AdminConfigurationBackupRestoreRequest());

        result.Success.Should().BeTrue();
        result.SafetyBackup.Should().NotBeNull();
        result.RuntimeResult.Should().NotBeNull();
        result.RuntimeResult!.ChangedPaths.Should().Contain("MqttVision:PublicBaseUrl");
        services.Runtime.Current.PublicBaseUrl.Should().Be("http://before.example:5080");
        changed.Should().NotBeNull();
        changed!.ChangedPaths.Should().Contain("MqttVision:PublicBaseUrl");

        var restoredCabinet = await services.Cabinets.GetAsync("cabinet-a");
        restoredCabinet.Terminals[0].ExpectedWireMarker.Should().Be("001/A");
        File.ReadAllText(services.Runtime.LocalConfigPath).Should().Contain("before.example");
    }

    [Fact]
    public async Task RestoreAsync_rejects_invalid_backup_id()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path, CreateOptions());

        var result = await services.Backups.RestoreAsync(
            "../outside",
            new AdminConfigurationBackupRestoreRequest());

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue =>
            issue.Path == "backupId" &&
            issue.Message.Contains("不合法"));
    }

    [Fact]
    public async Task RestoreAsync_can_block_warning_restore()
    {
        using var sandbox = new TestContentRoot();
        var services = CreateServices(sandbox.Path, CreateOptions());
        await services.Cabinets.SaveAsync(new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = "001/A" },
                new CabinetTerminalEditorRow { TerminalNumber = 2, ExpectedWireMarker = "001/A" }
            ]
        });
        var backup = await services.Backups.CreateAsync(new AdminConfigurationBackupCreateRequest());
        backup.Backup.Should().NotBeNull();

        var result = await services.Backups.RestoreAsync(
            backup.Backup!.BackupId,
            new AdminConfigurationBackupRestoreRequest
            {
                AllowWarnings = false
            });

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Path == "warnings");
    }

    private static TestServices CreateServices(
        string contentRoot,
        MqttVisionServerOptions options)
    {
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
        return new TestServices(runtime, cabinets, paths, backups);
    }

    private static MqttVisionServerOptions CreateOptions() =>
        new()
        {
            PublicBaseUrl = "http://before.example:5080",
            StorageRoot = "runtime",
            Processing = new ProcessingOptions
            {
                CabinetConfigurationRoot = "Configuration"
            }
        };

    private sealed record TestServices(
        RuntimeConfigurationService Runtime,
        CabinetConfigurationAdminService Cabinets,
        ServerPathInitializer Paths,
        AdminConfigurationBackupService Backups);

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-backup-test-{Guid.NewGuid():N}");
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
