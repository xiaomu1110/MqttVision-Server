using Microsoft.Extensions.Options;

namespace MqttVision.Server.Configuration;

public sealed class ServerPathInitializer
{
    private readonly IHostEnvironment environment;
    private readonly MqttVisionServerOptions options;

    public ServerPathInitializer(
        IOptions<MqttVisionServerOptions> options,
        IHostEnvironment environment)
    {
        this.environment = environment;
        this.options = options.Value;
        StorageRoot = ResolvePath(this.options.StorageRoot);
        UploadsRoot = Path.Combine(StorageRoot, "uploads");
        ArchiveRoot = Path.Combine(StorageRoot, "archive");
        LogsRoot = Path.Combine(StorageRoot, "logs");
        CadImportsRoot = Path.Combine(StorageRoot, "cad-imports");
    }

    public string StorageRoot { get; }

    public string UploadsRoot { get; }

    public string ArchiveRoot { get; }

    public string LogsRoot { get; }

    public string CadImportsRoot { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(UploadsRoot);
        Directory.CreateDirectory(ArchiveRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(CadImportsRoot);
    }

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
}
