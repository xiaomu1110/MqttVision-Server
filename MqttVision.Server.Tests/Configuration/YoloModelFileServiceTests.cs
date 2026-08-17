using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Tests.Configuration;

public sealed class YoloModelFileServiceTests
{
    [Fact]
    public async Task List_includes_built_in_and_uploaded_models_and_marks_selected_model()
    {
        using var sandbox = new TestContentRoot();
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "Models"));
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Path, "Models", "built-in.onnx"), [1, 2, 3]);
        using var service = CreateService(sandbox.Path);

        var uploaded = await service.UploadAsync(
            "..\\uploaded.onnx",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([4, 5, 6])));

        var models = service.List(uploaded.RelativePath);

        models.Should().Contain(model => model.FileName == "built-in.onnx" && model.IsBuiltIn);
        models.Should().ContainSingle(model =>
            model.FileName == "uploaded.onnx" &&
            !model.IsBuiltIn &&
            model.IsSelected &&
            model.RelativePath == "runtime/models/uploaded.onnx");
        File.Exists(Path.Combine(sandbox.Path, "runtime", "models", "uploaded.onnx")).Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_adds_suffix_when_file_name_already_exists()
    {
        using var sandbox = new TestContentRoot();
        using var service = CreateService(sandbox.Path);

        await service.UploadAsync(
            "model.onnx",
            1,
            _ => Task.FromResult<Stream>(new MemoryStream([1])));
        var second = await service.UploadAsync(
            "model.onnx",
            1,
            _ => Task.FromResult<Stream>(new MemoryStream([2])));

        second.FileName.Should().StartWith("model-").And.EndWith(".onnx");
        second.RelativePath.Should().NotBe("runtime/models/model.onnx");
    }

    [Fact]
    public async Task UploadAsync_rejects_non_onnx_and_oversized_files()
    {
        using var sandbox = new TestContentRoot();
        using var service = CreateService(sandbox.Path);

        var wrongExtension = () => service.UploadAsync(
            "model.bin",
            1,
            _ => Task.FromResult<Stream>(new MemoryStream([1])));
        var oversized = () => service.UploadAsync(
            "model.onnx",
            YoloModelFileService.MaxUploadBytes + 1,
            _ => Task.FromResult<Stream>(Stream.Null));

        await wrongExtension.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("只支持上传 .onnx*");
        await oversized.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("模型文件超过限制*");
    }

    [Fact]
    public async Task UploadAsync_normalizes_windows_path_separators_in_file_name()
    {
        using var sandbox = new TestContentRoot();
        using var service = CreateService(sandbox.Path);

        var uploaded = await service.UploadAsync(
            "..\\nested\\detector.onnx",
            1,
            _ => Task.FromResult<Stream>(new MemoryStream([1])));

        uploaded.FileName.Should().Be("detector.onnx");
        uploaded.RelativePath.Should().Be("runtime/models/detector.onnx");
        File.Exists(Path.Combine(sandbox.Path, "runtime", "models", "detector.onnx")).Should().BeTrue();
    }

    private static YoloModelFileService CreateService(string contentRoot)
    {
        var environment = new TestHostEnvironment(contentRoot);
        var options = new MqttVisionServerOptions { StorageRoot = "runtime" };
        var paths = new ServerPathInitializer(Options.Create(options), environment);
        paths.EnsureDirectories();
        return new YoloModelFileService(environment, paths);
    }

    private sealed class TestContentRoot : IDisposable
    {
        public TestContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mqttvision-model-test-{Guid.NewGuid():N}");
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
