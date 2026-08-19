using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;
using MqttVision.Server.Application.Configuration;
using MqttVision.Server.Infrastructure.Storage;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MqttVision.Server.Application;

public sealed class DetectionPipeline : IDetectionPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<DetectionPipeline> logger;
    private readonly IDetectionStorage storage;
    private readonly IObjectDetector objectDetector;
    private readonly ITextRecognizer textRecognizer;
    private readonly RuntimeConfigurationService configuration;
    private readonly IHostEnvironment environment;
    private readonly VisualConfigurationMatcher configurationMatcher = new();

    public DetectionPipeline(
        ILogger<DetectionPipeline> logger,
        IDetectionStorage storage,
        IObjectDetector objectDetector,
        ITextRecognizer textRecognizer,
        RuntimeConfigurationService configuration,
        IHostEnvironment environment)
    {
        this.logger = logger;
        this.storage = storage;
        this.objectDetector = objectDetector;
        this.textRecognizer = textRecognizer;
        this.configuration = configuration;
        this.environment = environment;
    }

    public async Task<DetectionPipelineResult> ProcessAsync(
        DetectionTaskRecord record,
        CancellationToken cancellationToken)
    {
        var options = configuration.Current;
        var sourceImage = await storage.FindSourceImageAsync(
            record.TaskId,
            record.Image,
            options.PublicBaseUrl,
            cancellationToken);

        if (sourceImage is null)
        {
            throw new FileNotFoundException($"未找到任务图片。TaskId={record.TaskId}, Url={record.Image.Url}");
        }

        var workspace = storage.CreateTaskWorkspace(record.TaskId);
        if (options.Processing.EnablePlaceholderPipeline)
        {
            return await ProcessPlaceholderAsync(record, sourceImage, workspace, options, cancellationToken);
        }

        var detections = await objectDetector.DetectAsync(sourceImage.FilePath, options.Processing, cancellationToken);
        await SaveDetectionCropsAsync(sourceImage, workspace, detections, options, cancellationToken);

        var pairs = BuildPairs(detections, options.Processing);
        SavePairFolders(workspace, pairs);

        var ocrResults = await RunOcrAsync(workspace, detections, cancellationToken);
        var configurationComparison = await CompareWithConfigurationAsync(
            record,
            workspace,
            detections,
            pairs,
            ocrResults,
            options,
            cancellationToken);
        var visualPath = await SaveVisualSummaryAsync(
            sourceImage,
            workspace,
            detections,
            pairs,
            configurationComparison.Items,
            cancellationToken);
        var resultJsonUrl = storage.BuildPublicFileUrl(workspace.ResultJsonPath, options.PublicBaseUrl);
        var ocrResultJsonUrl = storage.BuildPublicFileUrl(workspace.OcrResultJsonPath, options.PublicBaseUrl);
        var configurationComparisonJsonUrl = storage.BuildPublicFileUrl(workspace.ConfigurationComparisonJsonPath, options.PublicBaseUrl);
        var reportUrl = storage.BuildPublicFileUrl(workspace.MarkdownReportPath, options.PublicBaseUrl);
        var visualSummaryUrl = storage.BuildPublicFileUrl(visualPath, options.PublicBaseUrl);
        var terminals = detections.Where(detection => detection.ClassId == 0).ToList();
        var wireMarkerTubes = detections.Where(detection => detection.ClassId == 1).ToList();
        var summary = new DetectionResultSummary
        {
            TerminalCount = terminals.Count,
            WireTagCount = wireMarkerTubes.Count,
            PairCount = pairs.Count,
            CorrectPairCount = pairs.Count(pair => pair.Category == "confirmed"),
            SuspectedErrorCount = pairs.Count(pair => pair.Category == "suspected-error"),
            EmptyTerminalCount = pairs.Count(pair => pair.Category == "empty-terminal"),
            OcrItemCount = ocrResults.Count(result => result.Status == "recognized"),
            ConfigurationMatchedCount = configurationComparison.MatchedCount,
            ConfigurationMismatchCount = configurationComparison.MismatchCount,
            ConfigurationUnrecognizedCount = configurationComparison.UnrecognizedCount,
            ModelIntegrationPending = false
        };
        var pipelineStatus = options.Processing.PaddleOcrEnabled
            ? "YoloCompletedOcrCompleted"
            : "YoloCompletedOcrPending";
        var pipelineMessage = configurationComparison.Location.Status switch
        {
            "no-configuration" => "检测完成：三轮线号管检索后未找到对应柜体配置，未进行接线比对。",
            "ambiguous" => "检测完成：线号管同时命中多个候选柜体配置，未进行接线比对。",
            "unresolved" => "检测完成：没有可用于定位的有效线号管，未进行接线比对。",
            _ when options.Processing.PaddleOcrEnabled =>
                $"检测完成：配置匹配 {summary.ConfigurationMatchedCount} 项，疑似错接 {summary.ConfigurationMismatchCount} 项，无法识别 {summary.ConfigurationUnrecognizedCount} 项。",
            _ => "YOLO 检测与配对已完成，PaddleOCR 服务化识别未启用。"
        };

        var report = new
        {
            schemaVersion = "1.0",
            taskId = record.TaskId,
            status = pipelineStatus,
            sourceImage = new
            {
                sourceImage.FilePath,
                sourceImage.RelativePath,
                sourceImage.Url,
                sourceImage.Sha256,
                sourceImage.Size
            },
            model = new
            {
                options.Processing.YoloOnnxModelPath,
                options.Processing.PaddleOcrModelDirectory,
                paddleOcrEnabled = options.Processing.PaddleOcrEnabled,
                paddleOcrEngine = options.Processing.PaddleOcrEnabled
                    ? options.Processing.PaddleOcrDeploymentMode
                    : "disabled",
                paddleOcrServiceUrl = options.Processing.PaddleOcrEnabled
                    ? options.Processing.PaddleOcrServiceUrl
                    : null
            },
            pairing = new
            {
                options.Processing.PairMaxDistancePixels,
                options.Processing.AmbiguousDistanceTolerancePixels,
                options.Processing.PairMaxHorizontalDistancePixels,
                options.Processing.PairMaxVerticalGapPixels
            },
            summary,
            detections = detections.Select(ToDetectionReportItem),
            crops = new
            {
                terminals = terminals.Select(ToDetectionReportItem),
                wireMarkerTubes = wireMarkerTubes.Select(ToDetectionReportItem)
            },
            pairs = new
            {
                confirmed = pairs.Where(pair => pair.Category == "confirmed").Select(ToPairReportItem),
                suspectedErrors = pairs.Where(pair => pair.Category == "suspected-error").Select(ToPairReportItem),
                emptyTerminals = pairs.Where(pair => pair.Category == "empty-terminal").Select(ToPairReportItem)
            },
            ocr = ocrResults.Select(ToOcrReportItem),
            configurationComparison,
            artifacts = new
            {
                resultJsonUrl,
                ocrResultJsonUrl,
                configurationComparisonJsonUrl,
                reportUrl,
                visualSummaryUrl
            },
            generatedAt = DateTimeOffset.Now,
            notes = new[]
            {
                "YOLOv8 ONNX 检测、端子/线号管裁剪、局部几何候选配对和可视化输出已完成。",
                options.Processing.PaddleOcrEnabled
                    ? "PaddleOCR 服务化识别节点已执行，测试配置 JSON 比对和配置感知复核已完成。"
                    : "PaddleOCR 服务化识别节点未启用，测试配置 JSON 比对仅能标记无法识别。"
            }
        };

        await storage.SaveJsonAsync(workspace.ResultJsonPath, report, cancellationToken);
        await storage.SaveTextAsync(
            workspace.MarkdownReportPath,
            BuildMarkdownReport(record, sourceImage, summary, resultJsonUrl, visualSummaryUrl),
            cancellationToken);

        logger.LogInformation(
            "Detection pipeline completed YOLO result. TaskId={TaskId}, Terminals={Terminals}, WireMarkerTubes={WireMarkerTubes}, Result={ResultJsonUrl}",
            record.TaskId,
            summary.TerminalCount,
            summary.WireTagCount,
            resultJsonUrl);

        return new DetectionPipelineResult(
            true,
            pipelineStatus,
            pipelineMessage,
            summary,
            resultJsonUrl,
            reportUrl,
            visualSummaryUrl,
            null,
            configurationComparison);
    }

    private async Task<DetectionPipelineResult> ProcessPlaceholderAsync(
        DetectionTaskRecord record,
        SourceImageFile sourceImage,
        DetectionTaskWorkspace workspace,
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var previewPath = CopySourcePreview(sourceImage, workspace);
        var resultJsonUrl = storage.BuildPublicFileUrl(workspace.ResultJsonPath, options.PublicBaseUrl);
        var reportUrl = storage.BuildPublicFileUrl(workspace.MarkdownReportPath, options.PublicBaseUrl);
        var visualSummaryUrl = storage.BuildPublicFileUrl(previewPath, options.PublicBaseUrl);
        var summary = new DetectionResultSummary
        {
            ModelIntegrationPending = true
        };

        var report = new
        {
            schemaVersion = "1.0",
            taskId = record.TaskId,
            status = "ModelIntegrationPending",
            sourceImage,
            summary,
            detections = Array.Empty<object>(),
            pairs = Array.Empty<object>(),
            artifacts = new
            {
                resultJsonUrl,
                reportUrl,
                visualSummaryUrl
            },
            generatedAt = DateTimeOffset.Now
        };

        await storage.SaveJsonAsync(workspace.ResultJsonPath, report, cancellationToken);
        await storage.SaveTextAsync(
            workspace.MarkdownReportPath,
            BuildMarkdownReport(record, sourceImage, summary, resultJsonUrl, visualSummaryUrl),
            cancellationToken);

        return new DetectionPipelineResult(
            true,
            "ModelIntegrationPending",
            "服务端检测流水线已生成占位结果文件，模型节点等待接入。",
            summary,
            resultJsonUrl,
            reportUrl,
            visualSummaryUrl,
            null);
    }

    private static string CopySourcePreview(SourceImageFile sourceImage, DetectionTaskWorkspace workspace)
    {
        var extension = Path.GetExtension(sourceImage.FilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var previewPath = Path.Combine(workspace.VisualsRoot, $"source-preview{extension}");
        File.Copy(sourceImage.FilePath, previewPath, true);
        return previewPath;
    }

    private async Task SaveDetectionCropsAsync(
        SourceImageFile sourceImage,
        DetectionTaskWorkspace workspace,
        IReadOnlyList<DetectedObject> detections,
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgb24>(sourceImage.FilePath, cancellationToken);

        foreach (var detection in detections)
        {
            var targetRoot = detection.ClassId == 0
                ? workspace.TerminalCropsRoot
                : workspace.WireTagCropsRoot;
            var prefix = detection.ClassId == 0 ? "terminal" : "wire-marker-tube";
            var filePath = Path.Combine(targetRoot, $"{prefix}-{detection.Id:000}.jpg");
            var cropRectangle = ToRectangle(detection.Box, image.Width, image.Height);

            using var crop = image.Clone(context => context.Crop(cropRectangle));
            await crop.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 92 }, cancellationToken);

            detection.CropPath = filePath;
            detection.CropRelativePath = Path.GetRelativePath(workspace.RootPath, filePath).Replace('\\', '/');
            detection.CropUrl = storage.BuildPublicFileUrl(filePath, options.PublicBaseUrl);
        }
    }

    private static List<DetectionPair> BuildPairs(
        IReadOnlyList<DetectedObject> detections,
        ProcessingOptions processing)
    {
        var terminals = detections
            .Where(detection => detection.ClassId == 0)
            .OrderBy(detection => detection.Box.X)
            .ThenBy(detection => detection.Box.Y)
            .ToList();

        var wireMarkerTubes = detections
            .Where(detection => detection.ClassId == 1)
            .OrderBy(detection => detection.Box.CenterX)
            .ToList();

        var candidates = terminals
            .SelectMany(terminal => wireMarkerTubes.Select(wireMarkerTube => BuildPairCandidate(terminal, wireMarkerTube)))
            .Where(candidate => IsCandidateAllowed(candidate, processing))
            .ToList();
        var candidatesByTerminal = candidates
            .GroupBy(candidate => candidate.Terminal.Id)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate.Score).ToList());
        var bestByWire = candidates
            .GroupBy(candidate => candidate.Wire.Id)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate.Score).First());
        var assignedByTerminal = new Dictionary<int, PairCandidate>();
        var usedWireIds = new HashSet<int>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Score))
        {
            if (assignedByTerminal.ContainsKey(candidate.Terminal.Id) ||
                !usedWireIds.Add(candidate.Wire.Id))
            {
                continue;
            }

            assignedByTerminal[candidate.Terminal.Id] = candidate;
        }
        var pairs = new List<DetectionPair>();

        foreach (var terminal in terminals)
        {
            if (!candidatesByTerminal.TryGetValue(terminal.Id, out var terminalCandidates) ||
                terminalCandidates.Count == 0 ||
                !assignedByTerminal.TryGetValue(terminal.Id, out var candidate))
            {
                pairs.Add(new DetectionPair
                {
                    PairIndex = pairs.Count + 1,
                    Category = "empty-terminal",
                    Terminal = terminal,
                    Reason = "端子附近未找到满足局部几何约束的线号管候选，暂判为空端子。"
                });
                continue;
            }

            var category = IsConfirmedCandidate(candidate, terminalCandidates, bestByWire, processing)
                ? "confirmed"
                : "suspected-error";

            pairs.Add(new DetectionPair
            {
                PairIndex = pairs.Count + 1,
                Category = category,
                Terminal = terminal,
                WireMarkerTube = candidate.Wire,
                DistancePixels = candidate.CenterDistance,
                HorizontalDistancePixels = candidate.HorizontalDistance,
                VerticalGapPixels = candidate.VerticalGap,
                Reason = BuildPairReason(category, candidate, terminalCandidates, processing)
            });
        }

        return pairs;
    }

    private static void SavePairFolders(DetectionTaskWorkspace workspace, IReadOnlyCollection<DetectionPair> pairs)
    {
        foreach (var pair in pairs)
        {
            var folderPath = Path.Combine(
                workspace.CropsRoot,
                "pairs",
                pair.Category,
                $"pair-{pair.PairIndex:000}");
            Directory.CreateDirectory(folderPath);

            if (!string.IsNullOrWhiteSpace(pair.Terminal.CropPath) && File.Exists(pair.Terminal.CropPath))
            {
                File.Copy(pair.Terminal.CropPath, Path.Combine(folderPath, "terminal.jpg"), true);
            }

            if (!string.IsNullOrWhiteSpace(pair.WireMarkerTube?.CropPath) && File.Exists(pair.WireMarkerTube.CropPath))
            {
                File.Copy(pair.WireMarkerTube.CropPath, Path.Combine(folderPath, "wire-marker-tube.jpg"), true);
            }

            pair.FolderPath = folderPath;
            pair.FolderRelativePath = Path.GetRelativePath(workspace.RootPath, folderPath).Replace('\\', '/');
        }
    }

    private async Task<List<OcrResult>> RunOcrAsync(
        DetectionTaskWorkspace workspace,
        IReadOnlyCollection<DetectedObject> detections,
        CancellationToken cancellationToken)
    {
        var results = new List<OcrResult>();
        foreach (var detection in detections.OrderBy(detection => detection.Id))
        {
            var targetType = ToTargetType(detection);
            if (string.IsNullOrWhiteSpace(detection.CropPath))
            {
                results.Add(new OcrResult
                {
                    DetectionId = detection.Id,
                    TargetType = targetType,
                    ImageRelativePath = detection.CropRelativePath,
                    ImageUrl = detection.CropUrl,
                    Status = "failed",
                    ErrorMessage = "Crop path is empty."
                });
                continue;
            }

            var recognition = await RecognizeWithRotationsAsync(
                workspace,
                detection,
                targetType,
                cancellationToken);
            results.Add(new OcrResult
            {
                DetectionId = detection.Id,
                TargetType = targetType,
                ImageRelativePath = detection.CropRelativePath,
                ImageUrl = detection.CropUrl,
                Status = recognition.Result.Status,
                RawText = recognition.Result.Text,
                NormalizedText = NormalizeOcrText(recognition.Result.Text),
                Confidence = recognition.Result.Confidence,
                RotationDegrees = recognition.RotationDegrees,
                ErrorMessage = recognition.Result.ErrorMessage
            });
        }

        await storage.SaveJsonAsync(workspace.OcrResultJsonPath, results, cancellationToken);
        return results;
    }

    private async Task<OcrRotationCandidate> RecognizeWithRotationsAsync(
        DetectionTaskWorkspace workspace,
        DetectedObject detection,
        string targetType,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OcrRotationCandidate>();
        foreach (var rotationDegrees in new[] { 0, 90, 180, 270 })
        {
            var imagePath = rotationDegrees == 0
                ? detection.CropPath!
                : await SaveRotatedOcrCandidateAsync(
                    workspace,
                    detection,
                    rotationDegrees,
                    cancellationToken);
            var recognition = await textRecognizer.RecognizeAsync(imagePath, cancellationToken);
            candidates.Add(new OcrRotationCandidate(
                rotationDegrees,
                ValidateTargetRecognition(targetType, recognition)));
        }

        return candidates
            .OrderByDescending(candidate => IsTargetTextValid(targetType, candidate.Result.Text))
            .ThenByDescending(candidate => HasPreferredWireShape(targetType, candidate.Result.Text))
            .ThenByDescending(candidate => candidate.Result.Status == "recognized")
            .ThenByDescending(candidate => candidate.Result.Confidence ?? 0d)
            .ThenByDescending(candidate => candidate.Result.Text?.Length ?? 0)
            .ThenBy(candidate => candidate.RotationDegrees == 0 ? 0 : 1)
            .First();
    }

    private static async Task<string> SaveRotatedOcrCandidateAsync(
        DetectionTaskWorkspace workspace,
        DetectedObject detection,
        int rotationDegrees,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workspace.CacheRoot, "ocr-rotations");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"{ToTargetType(detection)}-{detection.Id:000}-{rotationDegrees:000}.jpg");

        using var image = await Image.LoadAsync<Rgb24>(detection.CropPath!, cancellationToken);
        using var rotated = image.Clone(context => context.Rotate(rotationDegrees));
        await rotated.SaveAsJpegAsync(path, new JpegEncoder { Quality = 92 }, cancellationToken);
        return path;
    }

    private static TextRecognitionResult ValidateTargetRecognition(
        string targetType,
        TextRecognitionResult recognition)
    {
        if (string.IsNullOrWhiteSpace(recognition.Text) ||
            recognition.Status is "no-text" or "failed" or "skipped")
        {
            return recognition;
        }

        var text = NormalizeOcrText(recognition.Text);
        if (IsTargetTextValid(targetType, text))
        {
            return new TextRecognitionResult(
                recognition.Status,
                text,
                recognition.Confidence,
                recognition.ErrorMessage);
        }

        var message = targetType == "terminal"
            ? "OCR 文本不符合端子编号格式（数字或数字加字母）。"
            : "OCR 文本不符合线号管格式（应包含前编号/后编号及连接符）。";
        return TextRecognitionResult.Unrecognized(text, recognition.Confidence, message);
    }

    private static bool IsTargetTextValid(string targetType, string? text) =>
        targetType == "terminal"
            ? IsTerminalLabel(text)
            : IsCanonicalWireMarkerText(text);

    private static bool HasPreferredWireShape(string targetType, string? text) =>
        targetType != "wire-marker-tube" ||
        (!string.IsNullOrWhiteSpace(text) && text.Contains('/', StringComparison.Ordinal));

    private static bool IsTerminalLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var digitLength = value.TakeWhile(char.IsDigit).Count();
        return digitLength > 0 &&
            (digitLength == value.Length ||
             digitLength == value.Length - 1 && char.IsLetter(value[^1]));
    }

    private static bool IsCanonicalWireMarkerText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains('/', StringComparison.Ordinal) &&
        text.Contains('-', StringComparison.Ordinal);

    private async Task<ConfigurationComparisonResult> CompareWithConfigurationAsync(
        DetectionTaskRecord record,
        DetectionTaskWorkspace workspace,
        IReadOnlyCollection<DetectedObject> detections,
        IReadOnlyCollection<DetectionPair> pairs,
        IReadOnlyCollection<OcrResult> ocrResults,
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var configurations = await LoadCabinetConfigurationsAsync(options, cancellationToken);
        var configurationIndex = CabinetConfigurationIndex.Build(configurations);
        var location = configurationMatcher.Match(
            configurationIndex,
            BuildMarkerObservations(detections, ocrResults),
            record.CabinetId);
        logger.LogInformation(
            "Configuration location evaluated. TaskId={TaskId}, Status={Status}, CabinetId={CabinetId}, StripId={StripId}, MatchedMarkers={MatchedMarkers}/{ObservedMarkers}, Confidence={Confidence}",
            record.TaskId,
            location.Status,
            location.CabinetId,
            location.StripId,
            location.MatchedMarkerCount,
            location.ObservedMarkerCount,
            location.Confidence);
        var configuration = SelectConfiguration(configurations, location);
        if (configuration is null)
        {
            var unresolvedResult = new ConfigurationComparisonResult
            {
                CabinetId = string.Empty,
                ResolvedTerminalStartNumber = null,
                ResolvedTerminalEndNumber = null,
                AlignmentStrategy = location.Status switch
                {
                    "ambiguous" => "no-configuration-ambiguous",
                    "no-configuration" => "no-configuration-match",
                    _ => "no-configuration-unresolved"
                },
                Location = location,
                CheckedCount = 0,
                MatchedCount = 0,
                MismatchCount = 0,
                UnrecognizedCount = 0,
                Items = []
            };
            await storage.SaveJsonAsync(workspace.ConfigurationComparisonJsonPath, unresolvedResult, cancellationToken);
            return unresolvedResult;
        }

        var configuredTerminals = NormalizeConfiguredTerminals(configuration, location.StripId);
        var ocrByDetectionId = ocrResults.ToDictionary(result => result.DetectionId);
        var items = BuildMarkerDrivenComparisonItems(
            configurationIndex,
            configuration,
            location.StripId,
            configuredTerminals,
            pairs.OrderBy(pair => pair.PairIndex).ToList(),
            ocrByDetectionId);
        var resolvedTerminalNumbers = items
            .Where(item => item.TerminalNumber > 0)
            .Select(item => item.TerminalNumber)
            .ToArray();

        var result = new ConfigurationComparisonResult
        {
            CabinetId = configuration.CabinetId,
            ResolvedTerminalStartNumber = resolvedTerminalNumbers.Length == 0 ? null : resolvedTerminalNumbers.Min(),
            ResolvedTerminalEndNumber = resolvedTerminalNumbers.Length == 0 ? null : resolvedTerminalNumbers.Max(),
            AlignmentStrategy = "marker-owner-terminal-ocr",
            Location = location,
            CheckedCount = items.Count,
            MatchedCount = items.Count(item => item.Result == "matched"),
            MismatchCount = items.Count(item => item.Result == "mismatch"),
            UnrecognizedCount = items.Count(item => item.Result == "unrecognized"),
            Items = items
        };
        await storage.SaveJsonAsync(workspace.ConfigurationComparisonJsonPath, result, cancellationToken);
        return result;
    }

    private static IReadOnlyList<ConfigurationMarkerObservation> BuildMarkerObservations(
        IReadOnlyCollection<DetectedObject> detections,
        IReadOnlyCollection<OcrResult> ocrResults)
    {
        var wireDetections = detections
            .Where(detection => detection.ClassId == 1)
            .ToDictionary(detection => detection.Id);
        return ocrResults
            .Where(result =>
                string.Equals(result.TargetType, "wire-marker-tube", StringComparison.OrdinalIgnoreCase) &&
                result.Status == "recognized" &&
                wireDetections.ContainsKey(result.DetectionId))
            .Select(result =>
            {
                wireDetections.TryGetValue(result.DetectionId, out var detection);
                return new ConfigurationMarkerObservation(
                    result.DetectionId,
                    result.NormalizedText ?? result.RawText,
                    result.Confidence,
                    detection?.Box.CenterX ?? 0d,
                    detection?.Box.CenterY ?? 0d);
            })
            .ToArray();
    }

    private static CabinetConfiguration? SelectConfiguration(
        IReadOnlyList<CabinetConfiguration> configurations,
        ConfigurationLocationResult location)
    {
        if (!string.Equals(location.Status, "matched", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(location.CabinetId))
        {
            return null;
        }

        return configurations.FirstOrDefault(configuration =>
            string.Equals(configuration.CabinetId, location.CabinetId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<CabinetConfiguration>> LoadCabinetConfigurationsAsync(
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var root = options.Processing.CabinetConfigurationRoot;
        var rootPath = Path.IsPathRooted(root)
            ? root
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, root));
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<CabinetConfiguration>();
        }

        var configurations = new List<CabinetConfiguration>();
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var configuration = await JsonSerializer.DeserializeAsync<CabinetConfiguration>(
                    stream,
                    JsonOptions,
                    cancellationToken: cancellationToken);
                if (configuration is not null && !string.IsNullOrWhiteSpace(configuration.CabinetId))
                {
                    configurations.Add(configuration);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "跳过无法读取的柜体配置文件。Path={Path}", path);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "跳过无法打开的柜体配置文件。Path={Path}", path);
            }
        }

        return configurations;
    }

    private static List<ConfigurationComparisonItem> BuildMarkerDrivenComparisonItems(
        CabinetConfigurationIndex configurationIndex,
        CabinetConfiguration configuration,
        string? stripId,
        IReadOnlyList<CabinetTerminalConfiguration> configuredTerminals,
        IReadOnlyList<DetectionPair> pairs,
        IReadOnlyDictionary<int, OcrResult> ocrByDetectionId)
    {
        var items = new List<ConfigurationComparisonItem>(pairs.Count);
        var fallbackTerminals = configuredTerminals.ToArray();
        foreach (var pair in pairs)
        {
            ocrByDetectionId.TryGetValue(pair.Terminal.Id, out var terminalOcr);
            var actualTerminal = ResolveTerminalConfiguration(configuredTerminals, terminalOcr);
            var fallbackTerminal = pair.PairIndex > 0 && pair.PairIndex <= fallbackTerminals.Length
                ? fallbackTerminals[pair.PairIndex - 1]
                : null;
            var displayTerminal = actualTerminal ?? fallbackTerminal;
            var terminalNumber = displayTerminal?.TerminalNumber ?? pair.PairIndex;

            ocrByDetectionId.TryGetValue(pair.WireMarkerTube?.Id ?? -1, out var wireOcr);
            var actualMarker = wireOcr?.Status == "recognized"
                ? NormalizeOcrText(wireOcr.NormalizedText ?? wireOcr.RawText)
                : null;
            var markerResolution = actualMarker is null
                ? null
                : ResolveMarkerAgainstConfiguration(
                    configurationIndex,
                    configuration,
                    stripId,
                    actualMarker);
            var expectedMarkers = GetExpectedWireMarkers(displayTerminal)
                .Select(NormalizeWireMarkerForDisplay)
                .Where(marker => !string.IsNullOrWhiteSpace(marker))
                .Cast<string>()
                .ToArray();
            var expectedDisplayMarker = string.Join(" / ", expectedMarkers);
            var actualDisplayMarker = NormalizeWireMarkerForDisplay(
                markerResolution?.CanonicalMarker ?? wireOcr?.RawText ?? actualMarker);

            var result = "unrecognized";
            var message = "无法通过端子 OCR 和线号管 OCR 确认接线关系。";
            if (markerResolution is not null)
            {
                if (actualTerminal is null)
                {
                    message = "线号管已在 CAD 配置中定位，但端子 OCR 未识别出可验证的端子编号。";
                }
                else if (SameTerminalConfiguration(actualTerminal, markerResolution.Terminal))
                {
                    result = "matched";
                    message = "线号管在 CAD 中对应的端子与图片中实际识别端子一致。";
                }
                else
                {
                    result = "mismatch";
                    message = $"线号管在 CAD 中应接端子 {FormatTerminalLabel(markerResolution.Terminal)}，图片实际识别为端子 {FormatTerminalLabel(actualTerminal)}。";
                }
            }
            else if (actualMarker is not null)
            {
                message = "线号管 OCR 已得到结果，但在已定位的 CAD 端子排中未找到对应配置。";
            }
            else if (pair.WireMarkerTube is null &&
                     actualTerminal is not null &&
                     (displayTerminal?.IsExpectedEmpty == true || expectedMarkers.Length == 0))
            {
                result = "matched";
                message = "CAD 配置要求该端子为空，且图片中未识别到有效线号管。";
            }

            items.Add(new ConfigurationComparisonItem
            {
                PairIndex = pair.PairIndex,
                TerminalNumber = terminalNumber,
                TerminalDetectionId = pair.Terminal.Id,
                WireMarkerTubeDetectionId = pair.WireMarkerTube?.Id,
                PairCategory = pair.Category,
                ExpectedWireMarker = expectedDisplayMarker,
                ActualWireMarker = actualDisplayMarker,
                Confidence = CombineOcrConfidence(terminalOcr, wireOcr),
                OcrStatus = wireOcr?.Status ?? "missing",
                Result = result,
                Message = message
            });
        }

        return items;
    }

    private static CabinetTerminalConfiguration? ResolveTerminalConfiguration(
        IReadOnlyList<CabinetTerminalConfiguration> configuredTerminals,
        OcrResult? terminalOcr)
    {
        if (terminalOcr?.Status != "recognized")
        {
            return null;
        }

        var label = NormalizeTerminalLabel(terminalOcr.NormalizedText ?? terminalOcr.RawText);
        if (!IsTerminalLabel(label))
        {
            return null;
        }

        var exact = configuredTerminals.FirstOrDefault(terminal =>
            string.Equals(
                NormalizeTerminalLabel(terminal.TerminalLabel),
                label,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (!int.TryParse(label!.TakeWhile(char.IsDigit).ToArray(), out var number))
        {
            return null;
        }

        var numberMatches = configuredTerminals
            .Where(terminal => terminal.TerminalNumber == number)
            .ToArray();
        return numberMatches.Length == 1 ? numberMatches[0] : null;
    }

    private static MarkerResolution? ResolveMarkerAgainstConfiguration(
        CabinetConfigurationIndex configurationIndex,
        CabinetConfiguration configuration,
        string? stripId,
        string actualMarker)
    {
        foreach (var (candidate, method) in BuildMarkerHypotheses(actualMarker))
        {
            var occurrences = FindMarkerOccurrences(
                configurationIndex,
                configuration,
                stripId,
                candidate,
                method);
            if (occurrences.Count == 1)
            {
                var occurrence = occurrences[0];
                return new MarkerResolution(
                    occurrence.NormalizedMarker,
                    occurrence.Terminal,
                    method);
            }
        }

        return null;
    }

    private static IReadOnlyList<ConfigurationMarkerOccurrence> FindMarkerOccurrences(
        CabinetConfigurationIndex configurationIndex,
        CabinetConfiguration configuration,
        string? stripId,
        string marker,
        string method)
    {
        var occurrences = method switch
        {
            "exact" => configurationIndex.Find(marker),
            "loose" => configurationIndex.FindLoose(marker),
            _ => configurationIndex.FindFuzzy(marker)
        };
        return occurrences
            .Where(occurrence =>
                string.Equals(occurrence.Configuration.CabinetId, configuration.CabinetId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(stripId) ||
                 string.Equals(occurrence.Strip.StripId, stripId, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(occurrence => new
            {
                occurrence.Terminal.TerminalLabel,
                occurrence.Terminal.TerminalNumber,
                occurrence.NormalizedMarker
            })
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<(string Candidate, string Method)> BuildMarkerHypotheses(string marker)
    {
        var normalized = NormalizeOcrText(marker);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var candidates = new List<(string Candidate, string Method)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, string method)
        {
            var candidate = NormalizeOcrText(value);
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add($"{method}:{candidate}"))
            {
                candidates.Add((candidate, method));
            }
        }

        Add(normalized, "exact");
        Add(normalized, "loose");
        Add(normalized, "fuzzy");

        foreach (var slashPosition in Enumerable.Range(1, Math.Min(4, normalized.Length - 1)))
        {
            if (normalized[slashPosition] == '/')
            {
                continue;
            }

            var withSlash = normalized.Insert(slashPosition, "/");
            Add(withSlash, "exact");
            Add(withSlash, "loose");
            Add(withSlash, "fuzzy");
            var slashIndex = withSlash.IndexOf('/');
            if (slashIndex >= 0 && slashIndex + 2 < withSlash.Length &&
                withSlash[slashIndex + 1] == '1' && withSlash[slashIndex + 2] == '1')
            {
                var withoutDuplicateOne = withSlash.Remove(slashIndex + 1, 1);
                Add(withoutDuplicateOne, "exact");
                Add(withoutDuplicateOne, "loose");
                Add(withoutDuplicateOne, "fuzzy");
                Add(ReplaceOneNConfusion(withoutDuplicateOne), "exact");
                Add(ReplaceOneNConfusion(withoutDuplicateOne), "loose");
                Add(ReplaceOneNConfusion(withoutDuplicateOne), "fuzzy");
            }

            Add(ReplaceOneNConfusion(withSlash), "exact");
            Add(ReplaceOneNConfusion(withSlash), "loose");
            Add(ReplaceOneNConfusion(withSlash), "fuzzy");
        }

        return candidates;
    }

    private static string ReplaceOneNConfusion(string value)
    {
        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0 || slashIndex + 2 >= value.Length || value[slashIndex + 1] != '1')
        {
            return value;
        }

        var next = value[slashIndex + 2];
        if (next == '0')
        {
            return value[..(slashIndex + 2)] + "N" + value[(slashIndex + 3)..];
        }

        if (next == 'N')
        {
            return value[..(slashIndex + 2)] + "0" + value[(slashIndex + 3)..];
        }

        return value;
    }

    private static bool SameTerminalConfiguration(
        CabinetTerminalConfiguration left,
        CabinetTerminalConfiguration right) =>
        string.Equals(
            NormalizeTerminalLabel(left.TerminalLabel),
            NormalizeTerminalLabel(right.TerminalLabel),
            StringComparison.OrdinalIgnoreCase) ||
        left.TerminalNumber == right.TerminalNumber &&
        string.Equals(left.StripId, right.StripId, StringComparison.OrdinalIgnoreCase);

    private static double? CombineOcrConfidence(OcrResult? terminalOcr, OcrResult? wireOcr)
    {
        var values = new[] { terminalOcr?.Confidence, wireOcr?.Confidence }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static string? NormalizeTerminalLabel(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : new string(text.Trim().Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();

    private static string FormatTerminalLabel(CabinetTerminalConfiguration terminal) =>
        string.IsNullOrWhiteSpace(terminal.TerminalLabel)
            ? terminal.TerminalNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : terminal.TerminalLabel;

    private static IReadOnlyList<CabinetTerminalConfiguration> NormalizeConfiguredTerminals(
        CabinetConfiguration configuration,
        string? stripId)
    {
        var terminals = string.IsNullOrWhiteSpace(stripId)
            ? configuration.Terminals
            : configuration.TerminalStrips
                .FirstOrDefault(strip => string.Equals(strip.StripId, stripId, StringComparison.OrdinalIgnoreCase))
                ?.Terminals ?? [];
        return terminals
            .Where(terminal => terminal.TerminalNumber > 0 || !string.IsNullOrWhiteSpace(terminal.TerminalLabel))
            .OrderBy(terminal => terminal.SourceOrdinal)
            .ThenBy(terminal => terminal.TerminalNumber)
            .ToArray();
    }

    private static IReadOnlyList<string> GetExpectedWireMarkers(CabinetTerminalConfiguration? terminal)
    {
        if (terminal is null)
        {
            return [];
        }

        return (terminal.WireMarkers ?? [])
            .Concat([terminal.ExpectedWireMarker, terminal.LeftWireMarker, terminal.RightWireMarker])
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Select(marker => marker!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<string> SaveVisualSummaryAsync(
        SourceImageFile sourceImage,
        DetectionTaskWorkspace workspace,
        IReadOnlyCollection<DetectedObject> detections,
        IReadOnlyCollection<DetectionPair> pairs,
        IReadOnlyCollection<ConfigurationComparisonItem> comparisonItems,
        CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgb24>(sourceImage.FilePath, cancellationToken);
        var comparisonByPairIndex = comparisonItems.ToDictionary(item => item.PairIndex);

        foreach (var detection in detections)
        {
            var rectangle = ToRectangle(detection.Box, image.Width, image.Height);
            DrawRectangle(image, rectangle, new Rgb24(120, 160, 210), 2);
        }

        foreach (var pair in pairs)
        {
            comparisonByPairIndex.TryGetValue(pair.PairIndex, out var comparisonItem);
            var color = ToComparisonColor(comparisonItem?.Result);
            var terminalRectangle = ToRectangle(pair.Terminal.Box, image.Width, image.Height);

            DrawRectangle(image, terminalRectangle, color, 6);

            if (pair.WireMarkerTube is null ||
                comparisonItem?.WireMarkerTubeDetectionId is null)
            {
                continue;
            }

            var wireMarkerRectangle = ToRectangle(pair.WireMarkerTube.Box, image.Width, image.Height);
            DrawRectangle(image, wireMarkerRectangle, color, 6);

            DrawLine(
                image,
                (int)MathF.Round(pair.Terminal.Box.CenterX),
                (int)MathF.Round(pair.Terminal.Box.Bottom),
                (int)MathF.Round(pair.WireMarkerTube.Box.CenterX),
                (int)MathF.Round(pair.WireMarkerTube.Box.Y),
                color,
                8);
        }

        var visualPath = Path.Combine(workspace.VisualsRoot, "detection-overlay.jpg");
        await image.SaveAsJpegAsync(visualPath, new JpegEncoder { Quality = 92 }, cancellationToken);
        return visualPath;
    }

    private static Rgb24 ToComparisonColor(string? result) =>
        result switch
        {
            "matched" => new Rgb24(20, 190, 100),
            "mismatch" => new Rgb24(230, 70, 55),
            "unrecognized" => new Rgb24(245, 175, 40),
            _ => new Rgb24(120, 160, 210)
        };

    private static string BuildMarkdownReport(
        DetectionTaskRecord record,
        SourceImageFile sourceImage,
        DetectionResultSummary summary,
        string resultJsonUrl,
        string visualSummaryUrl) =>
        $"""
        # MqttVision Detection Report

        ## Task

        - TaskId: {record.TaskId}
        - DeviceId: {record.DeviceId}
        - SiteId: {record.SiteId}
        - CabinetId: {record.CabinetId}
        - Status: {(summary.ModelIntegrationPending ? "ModelIntegrationPending" : "YoloCompletedOcrPending")}

        ## Source Image

        - File: {sourceImage.FilePath}
        - Url: {sourceImage.Url}
        - Sha256: {sourceImage.Sha256}
        - Size: {sourceImage.Size} bytes

        ## Detection Summary

        - Terminals: {summary.TerminalCount}
        - Wire marker tubes: {summary.WireTagCount}
        - Confirmed pairs: {summary.CorrectPairCount}
        - Suspected errors: {summary.SuspectedErrorCount}
        - Empty terminals: {summary.EmptyTerminalCount}
        - Configuration matched: {summary.ConfigurationMatchedCount}
        - Configuration mismatches: {summary.ConfigurationMismatchCount}
        - Unrecognized wire markers: {summary.ConfigurationUnrecognizedCount}

        ## Artifacts

        - Result JSON: {resultJsonUrl}
        - Visual Summary: {visualSummaryUrl}

        ## Next Integration Points

        - Load YOLO ONNX model and run terminal / wire-tag object detection.
        - Crop all terminal and wire-tag regions into the cache folders.
        - Pair terminals and wire tags by local geometry candidates, then verify with OCR and cabinet configuration.
        - Run PaddleOCR on paired and unpaired crops.
        - Compare OCR wire-tag numbers against cabinet configuration JSON.
        - Generate final industrial archive and visual overlay.
        """;

    private static object ToDetectionReportItem(DetectedObject detection) =>
        new
        {
            detection.Id,
            detection.ClassId,
            detection.ClassName,
            confidence = Math.Round(detection.Confidence, 4),
            box = detection.Box,
            detection.CropRelativePath,
            detection.CropUrl
        };

    private static object ToPairReportItem(DetectionPair pair) =>
        new
        {
            pair.PairIndex,
            pair.Category,
            terminalId = pair.Terminal.Id,
            wireMarkerTubeId = pair.WireMarkerTube?.Id,
            distancePixels = pair.DistancePixels is null ? (double?)null : Math.Round(pair.DistancePixels.Value, 2),
            horizontalDistancePixels = pair.HorizontalDistancePixels is null ? (double?)null : Math.Round(pair.HorizontalDistancePixels.Value, 2),
            verticalGapPixels = pair.VerticalGapPixels is null ? (double?)null : Math.Round(pair.VerticalGapPixels.Value, 2),
            pair.Reason,
            pair.FolderRelativePath
        };

    private static object ToOcrReportItem(OcrResult result) =>
        new
        {
            result.DetectionId,
            result.TargetType,
            result.ImageRelativePath,
            result.ImageUrl,
            result.Status,
            result.RawText,
            result.NormalizedText,
            result.RotationDegrees,
            confidence = result.Confidence is null ? (double?)null : Math.Round(result.Confidence.Value, 4),
            result.ErrorMessage
        };

    private static PairCandidate BuildPairCandidate(DetectedObject terminal, DetectedObject wireMarkerTube)
    {
        var horizontalDistance = Math.Abs(terminal.Box.CenterX - wireMarkerTube.Box.CenterX);
        var verticalGap = ComputeDirectionalVerticalGap(terminal.Box, wireMarkerTube.Box);
        var centerDistance = ComputeCenterDistance(terminal, wireMarkerTube);
        var horizontalGap = ComputeHorizontalGap(terminal.Box, wireMarkerTube.Box);
        var overlapRatio = ComputeHorizontalOverlapRatio(terminal.Box, wireMarkerTube.Box);
        var edgePenalty = wireMarkerTube.Box.X <= 2 ? 35 : 0;
        var score = horizontalGap * 2.5
            + horizontalDistance * 0.35
            + verticalGap * 0.2
            - overlapRatio * 45
            + edgePenalty;

        return new PairCandidate(
            terminal,
            wireMarkerTube,
            centerDistance,
            horizontalDistance,
            verticalGap,
            horizontalGap,
            overlapRatio,
            wireMarkerTube.Box.X <= 2,
            score);
    }

    private static bool IsCandidateAllowed(
        PairCandidate candidate,
        ProcessingOptions processing) =>
        (candidate.HorizontalGap <= processing.PairMaxHorizontalDistancePixels ||
            candidate.HorizontalDistance <= processing.PairMaxDistancePixels) &&
        candidate.VerticalGap <= processing.PairMaxVerticalGapPixels;

    private static bool IsConfirmedCandidate(
        PairCandidate candidate,
        IReadOnlyList<PairCandidate> terminalCandidates,
        IReadOnlyDictionary<int, PairCandidate> bestByWire,
        ProcessingOptions processing)
    {
        if (!bestByWire.TryGetValue(candidate.Wire.Id, out var wireBest) ||
            wireBest.Terminal.Id != candidate.Terminal.Id)
        {
            return false;
        }

        var nextTerminalCandidate = terminalCandidates.Skip(1).FirstOrDefault();
        var terminalMargin = nextTerminalCandidate is null
            ? double.PositiveInfinity
            : nextTerminalCandidate.Score - candidate.Score;
        var hasEnoughMargin = terminalMargin >= processing.AmbiguousDistanceTolerancePixels;

        return hasEnoughMargin &&
            !candidate.IsEdgeClipped &&
            candidate.OverlapRatio >= 0.18;
    }

    private static string BuildPairReason(
        string category,
        PairCandidate candidate,
        IReadOnlyList<PairCandidate> terminalCandidates,
        ProcessingOptions processing)
    {
        if (category == "confirmed")
        {
            return "端子与线号管在局部几何候选图中互为最佳，且候选分差满足确认阈值。";
        }

        var nextCandidate = terminalCandidates.Skip(1).FirstOrDefault();
        if (candidate.IsEdgeClipped)
        {
            return "候选线号管贴近图像边缘，可能是不完整目标或边缘透视目标，保留为疑似。";
        }

        if (nextCandidate is not null &&
            nextCandidate.Score - candidate.Score < processing.AmbiguousDistanceTolerancePixels)
        {
            return "端子存在多个几何得分接近的线号管候选，保留为疑似。";
        }

        return "端子与线号管不是互为唯一最佳候选，保留为疑似等待 OCR/配置校验。";
    }

    private static double ComputeDirectionalVerticalGap(DetectionBox terminal, DetectionBox wireMarkerTube)
    {
        if (wireMarkerTube.Y >= terminal.Bottom)
        {
            return wireMarkerTube.Y - terminal.Bottom;
        }

        if (terminal.Y >= wireMarkerTube.Bottom)
        {
            return terminal.Y - wireMarkerTube.Bottom;
        }

        return 0;
    }

    private static double ComputeHorizontalGap(DetectionBox first, DetectionBox second)
    {
        if (first.Right < second.X)
        {
            return second.X - first.Right;
        }

        if (second.Right < first.X)
        {
            return first.X - second.Right;
        }

        return 0;
    }

    private static double ComputeHorizontalOverlapRatio(DetectionBox first, DetectionBox second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X));
        var baseline = Math.Max(1, Math.Min(first.Width, second.Width));
        return overlap / baseline;
    }

    private static double Median(IEnumerable<float> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static double ComputeCenterDistance(DetectedObject terminal, DetectedObject wireMarkerTube)
    {
        var dx = terminal.Box.CenterX - wireMarkerTube.Box.CenterX;
        var dy = terminal.Box.CenterY - wireMarkerTube.Box.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string ToTargetType(DetectedObject detection) =>
        detection.ClassId == 0 ? "terminal" : "wire-marker-tube";

    private static string? NormalizeOcrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return CollapseDuplicateSlashes(string.Concat(text
            .Select(character => character switch
            {
                '／' => '/',
                '–' or '—' or '－' => '-',
                '＇' or '\'' or '’' or '‘' => '/',
                _ => character
            })
            .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant());
    }

    private static string? NormalizeWireMarkerForDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return string.Concat(text
                .Select(character => character switch
                {
                    '／' => '/',
                    '–' or '—' or '－' => '-',
                    '＇' or '’' or '‘' => '\'',
                    _ => character
                })
                .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
    }

    private static string CollapseDuplicateSlashes(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var previousWasSlash = false;
        foreach (var character in value)
        {
            if (character == '/')
            {
                if (previousWasSlash)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static Rectangle ToRectangle(DetectionBox box, int imageWidth, int imageHeight)
    {
        var left = Math.Clamp((int)MathF.Floor(box.X), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)MathF.Floor(box.Y), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)MathF.Ceiling(box.Right), left + 1, imageWidth);
        var bottom = Math.Clamp((int)MathF.Ceiling(box.Bottom), top + 1, imageHeight);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static void DrawRectangle(Image<Rgb24> image, Rectangle rectangle, Rgb24 color, int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            var left = Math.Clamp(rectangle.Left + offset, 0, image.Width - 1);
            var right = Math.Clamp(rectangle.Right - 1 - offset, 0, image.Width - 1);
            var top = Math.Clamp(rectangle.Top + offset, 0, image.Height - 1);
            var bottom = Math.Clamp(rectangle.Bottom - 1 - offset, 0, image.Height - 1);

            for (var x = left; x <= right; x++)
            {
                image[x, top] = color;
                image[x, bottom] = color;
            }

            for (var y = top; y <= bottom; y++)
            {
                image[left, y] = color;
                image[right, y] = color;
            }
        }
    }

    private static void DrawLine(
        Image<Rgb24> image,
        int x0,
        int y0,
        int x1,
        int y1,
        Rgb24 color,
        int thickness)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            DrawPoint(image, x0, y0, color, thickness);

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void DrawPoint(Image<Rgb24> image, int centerX, int centerY, Rgb24 color, int thickness)
    {
        var radius = Math.Max(1, thickness / 2);
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            if (y < 0 || y >= image.Height)
            {
                continue;
            }

            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                if (x >= 0 && x < image.Width)
                {
                    image[x, y] = color;
                }
            }
        }
    }

    private sealed record MarkerResolution(
        string CanonicalMarker,
        CabinetTerminalConfiguration Terminal,
        string MatchMethod);

    private sealed record OcrRotationCandidate(
        int RotationDegrees,
        TextRecognitionResult Result);

    private sealed record PairCandidate(
        DetectedObject Terminal,
        DetectedObject Wire,
        double CenterDistance,
        double HorizontalDistance,
        double VerticalGap,
        double HorizontalGap,
        double OverlapRatio,
        bool IsEdgeClipped,
        double Score);
}
