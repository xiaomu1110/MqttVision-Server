using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;

namespace MqttVision.Server.Infrastructure.Vision;

public sealed class CommandLineTextRecognizer : ITextRecognizer
{
    private readonly ILogger<CommandLineTextRecognizer> logger;
    private readonly MqttVisionServerOptions options;

    public CommandLineTextRecognizer(
        ILogger<CommandLineTextRecognizer> logger,
        IOptions<MqttVisionServerOptions> options)
    {
        this.logger = logger;
        this.options = options.Value;
    }

    public async Task<TextRecognitionResult> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var processing = options.Processing;
        if (!processing.PaddleOcrEnabled)
        {
            return TextRecognitionResult.Skipped("PaddleOCR is disabled by configuration.");
        }

        if (string.IsNullOrWhiteSpace(processing.PaddleOcrCommand))
        {
            return TextRecognitionResult.Skipped("OCR worker command is not configured.");
        }

        if (!File.Exists(imagePath))
        {
            return TextRecognitionResult.Failed($"OCR image file not found: {imagePath}");
        }

        var outputDirectory = CreateOcrOutputDirectory();
        var arguments = BuildArguments(
            processing.PaddleOcrArgumentsTemplate,
            imagePath,
            outputDirectory);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = processing.PaddleOcrCommand,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(processing.PaddleOcrWorkingDirectory)
                ? Environment.CurrentDirectory
                : processing.PaddleOcrWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        ConfigureProcessEnvironment(process.StartInfo, processing);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to start OCR worker. Command={Command}, Arguments={Arguments}",
                processing.PaddleOcrCommand,
                arguments);
            return TextRecognitionResult.Failed(ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, processing.PaddleOcrTimeoutSeconds));
        var completed = await Task.WhenAny(
            waitTask,
            Task.Delay(timeout, cancellationToken));

        if (completed != waitTask)
        {
            TryKill(process);
            return TextRecognitionResult.Failed($"PaddleOCR command timed out after {timeout.TotalSeconds:0} seconds.");
        }

        await waitTask;

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return TextRecognitionResult.Failed(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"OCR worker exited with code {process.ExitCode}."
                    : stderr.Trim());
        }

        var stdoutResult = ParseOutput(stdout);
        if (stdoutResult.Status == "recognized" || stdoutResult.Status == "failed")
        {
            return stdoutResult;
        }

        return TryParseOutputDirectory(outputDirectory, out var fileResult)
            ? fileResult
            : stdoutResult;
    }

    private static string BuildArguments(string template, string imagePath, string outputDirectory)
    {
        var argumentTemplate = string.IsNullOrWhiteSpace(template)
            ? "{image}"
            : template;
        return argumentTemplate
            .Replace("\"{image}\"", Quote(imagePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{image}", Quote(imagePath), StringComparison.OrdinalIgnoreCase)
            .Replace("\"{output}\"", Quote(outputDirectory), StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", Quote(outputDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string CreateOcrOutputDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "MqttVision",
            "ocr",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ConfigureProcessEnvironment(
        ProcessStartInfo startInfo,
        ProcessingOptions processing)
    {
        if (string.IsNullOrWhiteSpace(processing.PaddleOcrAdditionalPath))
        {
            return;
        }

        var currentPath = startInfo.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? processing.PaddleOcrAdditionalPath
            : $"{processing.PaddleOcrAdditionalPath};{currentPath}";
    }

    private static TextRecognitionResult ParseOutput(string stdout)
    {
        var output = stdout.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return TextRecognitionResult.NoText();
        }

        var lastLine = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(lastLine))
        {
            return TextRecognitionResult.NoText();
        }

        if (TryParseJson(lastLine, out var jsonResult))
        {
            return jsonResult;
        }

        return TextRecognitionResult.Recognized(lastLine, null);
    }

    private static bool TryParseJson(string line, out TextRecognitionResult result)
    {
        result = TextRecognitionResult.NoText();

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var text = ReadOcrText(root);
            var confidence = ReadOcrConfidence(root);
            var error = ReadString(root, "error") ?? ReadString(root, "Error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                result = TextRecognitionResult.Failed(error);
                return true;
            }

            result = string.IsNullOrWhiteSpace(text)
                ? TextRecognitionResult.NoText()
                : TextRecognitionResult.Recognized(text, confidence);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseOutputDirectory(string outputDirectory, out TextRecognitionResult result)
    {
        result = TextRecognitionResult.NoText();
        if (!Directory.Exists(outputDirectory))
        {
            return false;
        }

        var jsonFile = Directory
            .EnumerateFiles(outputDirectory, "*.json", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (jsonFile is null)
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(jsonFile.FullName, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var text = ReadOcrText(root);
            var confidence = ReadOcrConfidence(root);
            result = string.IsNullOrWhiteSpace(text)
                ? TextRecognitionResult.NoText()
                : TextRecognitionResult.Recognized(text, confidence);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            result = TextRecognitionResult.Failed(ex.Message);
            return true;
        }
    }

    private static string? ReadOcrText(JsonElement root)
    {
        if (TryGetObject(root, "res", out var res))
        {
            return ReadOcrText(res);
        }

        return ReadString(root, "text") ??
            ReadString(root, "Text") ??
            ReadString(root, "rec_text") ??
            ReadString(root, "recText") ??
            ReadStringArray(root, "rec_texts");
    }

    private static double? ReadOcrConfidence(JsonElement root)
    {
        if (TryGetObject(root, "res", out var res))
        {
            return ReadOcrConfidence(res);
        }

        return ReadDouble(root, "confidence") ??
            ReadDouble(root, "Confidence") ??
            ReadDouble(root, "rec_score") ??
            ReadDouble(root, "recScore") ??
            ReadDoubleArrayAverage(root, "rec_scores");
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement property) =>
        root.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.Object;

    private static string? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Concat(texts);
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static double? ReadDoubleArrayAverage(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = property
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value)
                ? value
                : (double?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        return values.Length == 0 ? null : values.Average();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
