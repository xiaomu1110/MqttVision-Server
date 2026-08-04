using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class CabinetConfigurationAdminServiceTests
{
    [Fact]
    public async Task SaveAsync_writes_sorted_cabinet_json_and_normalizes_empty_wire_marker()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path);
        var form = new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 2, ExpectedWireMarker = " 002/A " },
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = " " }
            ]
        };

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        var saved = await service.GetAsync("cabinet-a");
        saved.Terminals.Select(terminal => terminal.TerminalNumber).Should().Equal(1, 2);
        saved.Terminals[0].ExpectedWireMarker.Should().BeNull();
        saved.Terminals[1].ExpectedWireMarker.Should().Be("002/A");
        File.Exists(Path.Combine(sandbox.Path, "Configuration", "cabinet-a.json")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_rejects_duplicate_terminal_number()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path);
        var form = new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1 },
                new CabinetTerminalEditorRow { TerminalNumber = 1 }
            ]
        };

        var result = await service.SaveAsync(form);

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Path == "terminals" && !issue.IsWarning);
    }

    [Fact]
    public async Task SaveAsync_allows_duplicate_wire_marker_as_warning()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path);
        var form = new CabinetConfigurationEditorForm
        {
            CabinetId = "cabinet-a",
            TerminalStartNumber = 1,
            Terminals =
            [
                new CabinetTerminalEditorRow { TerminalNumber = 1, ExpectedWireMarker = "001/A" },
                new CabinetTerminalEditorRow { TerminalNumber = 2, ExpectedWireMarker = "001/A" }
            ]
        };

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        result.Issues.Should().Contain(issue => issue.IsWarning && issue.Message.Contains("多个端子"));
    }

    [Fact]
    public async Task GetAsync_rejects_unsafe_cabinet_id()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path);

        var act = () => service.GetAsync("../outside");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*柜体编号*");
    }

    private static CabinetConfigurationAdminService CreateService(string contentRoot)
    {
        var options = Options.Create(new MqttVisionServerOptions
        {
            Processing = new ProcessingOptions
            {
                CabinetConfigurationRoot = "Configuration"
            }
        });
        var runtimeConfiguration = new RuntimeConfigurationService(
            options,
            new TestHostEnvironment(contentRoot),
            NullLogger<RuntimeConfigurationService>.Instance);
        return new CabinetConfigurationAdminService(
            runtimeConfiguration,
            new TestHostEnvironment(contentRoot));
    }

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-cabinet-test-{Guid.NewGuid():N}");
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
