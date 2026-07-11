using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MqttVision.Server.Tests.Configuration;

public class RuntimeConfigurationServiceTests
{
    [Fact]
    public async Task SaveAsync_rejects_invalid_configuration_without_writing_file()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Mqtt.BrokerPort = 70000;

        var result = await service.SaveAsync(form);

        result.Success.Should().BeFalse();
        File.Exists(service.LocalConfigPath).Should().BeFalse();
        service.Current.Mqtt.BrokerPort.Should().Be(1883);
    }

    [Fact]
    public async Task SaveAsync_writes_local_yaml_and_updates_current_snapshot()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.PublicBaseUrl = "http://10.0.0.5:5080";
        form.Processing.PaddleOcrEnabled = true;
        form.Processing.PaddleOcrServiceUrl = "http://127.0.0.1:8080/ocr";

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        result.ChangedPaths.Should().Contain("MqttVision:PublicBaseUrl");
        service.Current.PublicBaseUrl.Should().Be("http://10.0.0.5:5080");
        File.ReadAllText(service.LocalConfigPath).Should().Contain("PublicBaseUrl: \"http://10.0.0.5:5080\"");
    }

    [Fact]
    public async Task SaveAsync_uses_local_override_next_to_environment_config()
    {
        using var sandbox = new TestContentRoot();
        var configuredPath = System.IO.Path.Combine(sandbox.Path, "server.yaml");
        await File.WriteAllTextAsync(
            configuredPath,
            """
            MqttVision:
              PublicBaseUrl: "http://from-env:5080"
            """);
        var previousConfigPath = Environment.GetEnvironmentVariable(MqttVisionYamlConfiguration.ConfigPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(MqttVisionYamlConfiguration.ConfigPathEnvironmentVariable, configuredPath);

        try
        {
            var service = CreateService(
                sandbox.Path,
                new MqttVisionServerOptions
                {
                    PublicBaseUrl = "http://from-env:5080"
                });
            service.LocalConfigPath.Should().Be(System.IO.Path.Combine(sandbox.Path, "mqttvision.local.yaml"));

            var form = AdminConfigurationForm.FromOptions(service.Current);
            form.PublicBaseUrl = "http://from-admin:5080";
            var result = await service.SaveAsync(form);

            result.Success.Should().BeTrue();
            File.Exists(service.LocalConfigPath).Should().BeTrue();

            var configuration = new ConfigurationBuilder()
                .AddMqttVisionYaml(sandbox.Path)
                .Build();

            configuration["MqttVision:PublicBaseUrl"].Should().Be("http://from-admin:5080");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MqttVisionYamlConfiguration.ConfigPathEnvironmentVariable, previousConfigPath);
        }
    }

    [Fact]
    public async Task SaveAsync_detects_all_ocr_field_changes()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Processing.PaddleOcrModelDirectory = "ocr-models";
        form.Processing.PaddleOcrUseDocOrientationClassify = false;
        form.Processing.PaddleOcrUseDocUnwarping = true;
        form.Processing.PaddleOcrUseTextlineOrientation = false;
        form.Processing.PaddleOcrCommand = "paddlex";
        form.Processing.PaddleOcrArgumentsTemplate = "--image {image}";
        form.Processing.PaddleOcrWorkingDirectory = "ocr-work";
        form.Processing.PaddleOcrAdditionalPath = "ocr-bin";

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        result.ChangedPaths.Should().Contain(
            [
                "MqttVision:Processing:PaddleOcrModelDirectory",
                "MqttVision:Processing:PaddleOcrUseDocOrientationClassify",
                "MqttVision:Processing:PaddleOcrUseDocUnwarping",
                "MqttVision:Processing:PaddleOcrUseTextlineOrientation",
                "MqttVision:Processing:PaddleOcrCommand",
                "MqttVision:Processing:PaddleOcrArgumentsTemplate",
                "MqttVision:Processing:PaddleOcrWorkingDirectory",
                "MqttVision:Processing:PaddleOcrAdditionalPath"
            ]);
        service.Current.Processing.PaddleOcrCommand.Should().Be("paddlex");
    }

    [Fact]
    public async Task SaveAsync_reports_auto_reconnect_for_mqtt_changes()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Mqtt.BrokerHost = "10.0.0.20";
        RuntimeConfigurationChangedEventArgs? changed = null;
        service.Changed += (_, args) => changed = args;

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("自动重连");
        result.ChangedPaths.Should().Contain("MqttVision:Mqtt:BrokerHost");
        changed.Should().NotBeNull();
        changed!.ChangedPaths.Should().Contain("MqttVision:Mqtt:BrokerHost");
    }

    [Fact]
    public async Task SaveAsync_reports_auto_model_load_for_yolo_changes()
    {
        using var sandbox = new TestContentRoot();
        var modelPath = System.IO.Path.Combine(sandbox.Path, "model.onnx");
        await File.WriteAllTextAsync(modelPath, "placeholder");
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Processing.EnablePlaceholderPipeline = false;
        form.Processing.YoloOnnxModelPath = "model.onnx";
        form.Processing.ConfidenceThreshold = 0.72f;

        var result = await service.SaveAsync(form);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("自动加载");
        result.ChangedPaths.Should().Contain("MqttVision:Processing:YoloOnnxModelPath");
        result.ChangedPaths.Should().Contain("MqttVision:Processing:ConfidenceThreshold");
    }

    [Fact]
    public async Task SaveAsync_rejects_missing_yolo_model_when_placeholder_is_disabled()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Processing.EnablePlaceholderPipeline = false;
        form.Processing.YoloOnnxModelPath = "missing.onnx";

        var result = await service.SaveAsync(form);

        result.Success.Should().BeFalse();
        result.Snapshot.Validation.Errors.Should().Contain(issue =>
            issue.Path == "MqttVision:Processing:YoloOnnxModelPath" &&
            issue.Message.Contains("不存在"));
    }

    [Fact]
    public void Validate_rejects_invalid_mqtt_topics()
    {
        using var sandbox = new TestContentRoot();
        var service = CreateService(sandbox.Path, new MqttVisionServerOptions());
        var form = AdminConfigurationForm.FromOptions(service.Current);
        form.Mqtt.TaskSubmitTopic = "mqttvision/a+/task";
        form.Mqtt.TaskProgressTopicTemplate = "mqttvision/{siteId}/+/progress";

        var result = service.Validate(form.ToOptions());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(issue =>
            issue.Path == "MqttVision:Mqtt:TaskSubmitTopic" &&
            issue.Message.Contains("通配符位置不合法"));
        result.Errors.Should().Contain(issue =>
            issue.Path == "MqttVision:Mqtt:TaskProgressTopicTemplate" &&
            issue.Message.Contains("发布主题不能包含"));
    }

    [Fact]
    public void FieldDescriptors_mark_storage_and_mqtt_as_not_hot_reload()
    {
        var fields = RuntimeConfigurationService.GetFieldDescriptors();

        fields.Single(field => field.Path == "MqttVision:StorageRoot").ApplyMode
            .Should().Be(ConfigurationApplyMode.RequiresRestart);
        fields.Single(field => field.Path == "MqttVision:Mqtt:BrokerHost").ApplyMode
            .Should().Be(ConfigurationApplyMode.RequiresReconnect);
        fields.Single(field => field.Path == "MqttVision:Processing:PaddleOcrServiceUrl").ApplyMode
            .Should().Be(ConfigurationApplyMode.HotReload);
    }

    private static RuntimeConfigurationService CreateService(
        string contentRoot,
        MqttVisionServerOptions options) =>
        new(
            Options.Create(options),
            new TestHostEnvironment(contentRoot),
            NullLogger<RuntimeConfigurationService>.Instance);

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-test-{Guid.NewGuid():N}");
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
