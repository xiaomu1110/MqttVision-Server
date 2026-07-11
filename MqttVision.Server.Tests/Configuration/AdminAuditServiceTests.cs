using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class AdminAuditServiceTests
{
    [Fact]
    public async Task RecordAsync_writes_jsonl_outside_public_storage_root()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions
        {
            StorageRoot = "runtime"
        });

        await service.RecordAsync(
            AdminAuditCategories.RuntimeConfiguration,
            "保存系统配置",
            AdminAuditOutcomes.Success,
            "配置已保存。",
            "系统配置",
            new Dictionary<string, string?> { ["变更字段数量"] = "2" });

        File.Exists(service.AuditFilePath).Should().BeTrue();
        service.AuditFilePath.Should().StartWith(Path.Combine(sandbox.Path, ".admin-audit"));
        service.AuditFilePath.Should().NotStartWith(Path.Combine(sandbox.Path, "runtime"));
        var content = await File.ReadAllTextAsync(service.AuditFilePath);
        content.Should().Contain("保存系统配置");
        content.Should().Contain("变更字段数量");
    }

    [Fact]
    public async Task ListAsync_returns_newest_entries_and_filters_category()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());

        await service.RecordAsync(
            AdminAuditCategories.RuntimeConfiguration,
            "保存系统配置",
            AdminAuditOutcomes.Success,
            "系统配置已保存。");
        await service.RecordAsync(
            AdminAuditCategories.Backup,
            "创建配置备份",
            AdminAuditOutcomes.Success,
            "配置备份已创建。");

        var all = await service.ListAsync(new AdminAuditQuery { Limit = 10 });
        var backup = await service.ListAsync(new AdminAuditQuery
        {
            Category = AdminAuditCategories.Backup,
            Limit = 10
        });

        all.Select(entry => entry.Action).Should().Equal("创建配置备份", "保存系统配置");
        backup.Should().ContainSingle();
        backup[0].Category.Should().Be(AdminAuditCategories.Backup);
    }

    [Fact]
    public async Task RecordAsync_normalizes_empty_actor_and_skips_empty_details()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());

        await service.RecordAsync(new AdminAuditWriteRequest
        {
            Category = AdminAuditCategories.Authentication,
            Action = "接口登录",
            Outcome = AdminAuditOutcomes.Failure,
            Message = "密码错误。",
            Actor = " ",
            Details = new Dictionary<string, string?>
            {
                ["保留"] = "有值",
                ["空值"] = " "
            }
        });

        var entries = await service.ListAsync(new AdminAuditQuery { Limit = 10 });

        entries.Should().ContainSingle();
        entries[0].Actor.Should().Be("管理员");
        entries[0].Details.Should().ContainKey("保留");
        entries[0].Details.Should().NotContainKey("空值");
    }

    [Fact]
    public async Task RecordAsync_adds_source_detail_and_list_clamps_large_limit()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        Directory.CreateDirectory(service.AuditRoot);
        await File.WriteAllTextAsync(service.AuditFilePath, "{broken json}" + Environment.NewLine);

        for (var index = 0; index < 520; index++)
        {
            await service.RecordAsync(
                AdminAuditCategories.Backup,
                "创建配置备份",
                AdminAuditOutcomes.Success,
                $"第 {index} 条。");
        }

        var entries = await service.ListAsync(new AdminAuditQuery { Limit = 1000 });

        entries.Should().HaveCount(500);
        entries[0].Message.Should().Be("第 519 条。");
        entries[0].Details.Should().Contain("来源", "页面");
    }

    private static AdminAuditService CreateService(
        string contentRoot,
        MqttVisionServerOptions options)
    {
        var environment = new TestHostEnvironment(contentRoot);
        var paths = new ServerPathInitializer(Options.Create(options), environment);
        return new AdminAuditService(paths, environment);
    }

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-audit-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
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
