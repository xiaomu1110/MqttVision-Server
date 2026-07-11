using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace MqttVision.Server.Configuration;

public static class MqttVisionYamlConfiguration
{
    public const string ConfigPathEnvironmentVariable = "MQTTVISION_CONFIG";

    public static IConfigurationBuilder AddMqttVisionYaml(
        this IConfigurationBuilder configuration,
        string contentRootPath)
    {
        foreach (var configPath in ResolveConfigFiles(contentRootPath))
        {
            configuration.AddInMemoryCollection(LoadValues(configPath));
        }

        return configuration;
    }

    public static string ResolveWritableLocalConfigPath(string contentRootPath)
    {
        var configuredPath = ResolveConfiguredPath();
        if (configuredPath is not null)
        {
            return ResolveConfiguredLocalOverridePath(configuredPath);
        }

        foreach (var root in EnumerateSearchRoots(contentRootPath))
        {
            var configPath = Path.Combine(root, "config", "mqttvision.yaml");
            var localConfigPath = Path.Combine(root, "config", "mqttvision.local.yaml");
            if (File.Exists(configPath) || File.Exists(localConfigPath))
            {
                return localConfigPath;
            }

            var rootConfigPath = Path.Combine(root, "mqttvision.yaml");
            var rootLocalConfigPath = Path.Combine(root, "mqttvision.local.yaml");
            if (File.Exists(rootConfigPath) || File.Exists(rootLocalConfigPath))
            {
                return rootLocalConfigPath;
            }
        }

        return Path.Combine(contentRootPath, "config", "mqttvision.local.yaml");
    }

    private static IReadOnlyList<string> ResolveConfigFiles(string contentRootPath)
    {
        var configuredPath = ResolveConfiguredPath();
        if (configuredPath is not null)
        {
            if (!File.Exists(configuredPath))
            {
                throw new FileNotFoundException(
                    $"YAML 配置文件不存在。请检查 {ConfigPathEnvironmentVariable}。",
                    configuredPath);
            }

            var localOverridePath = ResolveConfiguredLocalOverridePath(configuredPath);
            return IncludeExisting(configuredPath, localOverridePath);
        }

        foreach (var root in EnumerateSearchRoots(contentRootPath))
        {
            var configPath = Path.Combine(root, "config", "mqttvision.yaml");
            var localConfigPath = Path.Combine(root, "config", "mqttvision.local.yaml");
            var rootConfigPath = Path.Combine(root, "mqttvision.yaml");
            var rootLocalConfigPath = Path.Combine(root, "mqttvision.local.yaml");
            var existingPaths = new[]
                {
                    configPath,
                    localConfigPath,
                    rootConfigPath,
                    rootLocalConfigPath
                }
                .Where(File.Exists)
                .ToArray();

            if (existingPaths.Length > 0)
            {
                return existingPaths;
            }
        }

        return [];
    }

    private static IReadOnlyList<string> IncludeExisting(params string[] paths) =>
        paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

    private static string? ResolveConfiguredPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : ExpandPath(configuredPath);
    }

    private static string ResolveConfiguredLocalOverridePath(string configuredPath)
    {
        if (Path.GetFileName(configuredPath).Equals("mqttvision.local.yaml", StringComparison.OrdinalIgnoreCase))
        {
            return configuredPath;
        }

        return Path.Combine(
            Path.GetDirectoryName(configuredPath) ?? Directory.GetCurrentDirectory(),
            "mqttvision.local.yaml");
    }

    private static IEnumerable<string> EnumerateSearchRoots(string contentRootPath)
    {
        var roots = new[]
            {
                contentRootPath,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var current = new DirectoryInfo(root);
            while (current is not null)
            {
                yield return current.FullName;
                current = current.Parent;
            }
        }
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), expanded));
    }

    private static IDictionary<string, string?> LoadValues(string configPath)
    {
        using var reader = File.OpenText(configPath);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (yaml.Documents.Count > 0)
        {
            FlattenNode(yaml.Documents[0].RootNode, string.Empty, values);
        }

        return values;
    }

    private static void FlattenNode(
        YamlNode node,
        string keyPrefix,
        IDictionary<string, string?> values)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var child in mapping.Children)
                {
                    var key = ReadKey(child.Key);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var childPrefix = string.IsNullOrWhiteSpace(keyPrefix)
                        ? key
                        : $"{keyPrefix}:{key}";
                    FlattenNode(child.Value, childPrefix, values);
                }

                break;

            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    FlattenNode(sequence.Children[index], $"{keyPrefix}:{index}", values);
                }

                break;

            case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(keyPrefix):
                values[keyPrefix] = NormalizeScalarValue(scalar.Value);
                break;
        }
    }

    private static string ReadKey(YamlNode node) =>
        node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : node.ToString();

    private static string? NormalizeScalarValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(value) ||
            trimmed is "~" ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
    }
}
