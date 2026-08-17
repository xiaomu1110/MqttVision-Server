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
            "no-configuration" => "检测完成：三轮线号管检索后未找到对应 CAD 配置，未进行接线比对。",
            "ambiguous" => "检测完成：线号管同时命中多个候选 CAD 配置，未进行接线比对。",
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
        var pairs = new List<DetectionPair>();

        foreach (var terminal in terminals)
        {
            if (!candidatesByTerminal.TryGetValue(terminal.Id, out var terminalCandidates) || terminalCandidates.Count == 0)
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

            var candidate = terminalCandidates[0];
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
            if (string.IsNullOrWhiteSpace(detection.CropPath))
            {
                results.Add(new OcrResult
                {
                    DetectionId = detection.Id,
                    TargetType = ToTargetType(detection),
                    ImageRelativePath = detection.CropRelativePath,
                    ImageUrl = detection.CropUrl,
                    Status = "failed",
                    ErrorMessage = "Crop path is empty."
                });
                continue;
            }

            var recognition = await textRecognizer.RecognizeAsync(detection.CropPath, cancellationToken);
            results.Add(new OcrResult
            {
                DetectionId = detection.Id,
                TargetType = ToTargetType(detection),
                ImageRelativePath = detection.CropRelativePath,
                ImageUrl = detection.CropUrl,
                Status = recognition.Status,
                RawText = recognition.Text,
                NormalizedText = NormalizeOcrText(recognition.Text),
                Confidence = recognition.Confidence,
                ErrorMessage = recognition.ErrorMessage
            });
        }

        await storage.SaveJsonAsync(workspace.OcrResultJsonPath, results, cancellationToken);
        return results;
    }

    private async Task<ConfigurationComparisonResult> CompareWithConfigurationAsync(
        DetectionTaskRecord record,
        DetectionTaskWorkspace workspace,
        IReadOnlyCollection<DetectedObject> detections,
        IReadOnlyCollection<DetectionPair> pairs,
        IReadOnlyCollection<OcrResult> ocrResults,
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var configurations = await LoadCabinetConfigurationsAsync(record.CabinetId, options, cancellationToken);
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
        var candidates = new List<ConfigurationComparisonCandidate>();
        var orderedPairs = pairs.OrderBy(pair => pair.PairIndex).ToList();
        var locationMarkers = location.Candidates
            .Find(candidate =>
                string.Equals(candidate.CabinetId, configuration.CabinetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.StripId, location.StripId, StringComparison.OrdinalIgnoreCase))
            ?.MatchedMarkers
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var alignment = ResolveConfigurationAlignment(
            configuration,
            configuredTerminals,
            orderedPairs,
            ocrByDetectionId,
            locationMarkers,
            useTerminalStartHint: location.Status != "matched");

        foreach (var assignment in alignment.Assignments)
        {
            var pair = assignment.Pair;
            ocrByDetectionId.TryGetValue(pair.WireMarkerTube?.Id ?? -1, out var ocrResult);

            var expectedDisplayMarker = NormalizeWireMarkerForDisplay(assignment.Configuration?.ExpectedWireMarker);
            var expectedMarker = NormalizeOcrText(expectedDisplayMarker);
            var actualMarker = ocrResult?.Status == "recognized"
                ? NormalizeOcrText(ocrResult.NormalizedText ?? ocrResult.RawText)
                : null;
            var actualDisplayMarker = ocrResult?.Status == "recognized"
                ? NormalizeWireMarkerForDisplay(ocrResult.RawText ?? ocrResult.NormalizedText)
                : null;
            if (!string.IsNullOrWhiteSpace(expectedDisplayMarker) &&
                string.Equals(expectedMarker, actualMarker, StringComparison.OrdinalIgnoreCase))
            {
                actualDisplayMarker = expectedDisplayMarker;
            }

            var item = BuildConfigurationComparisonItem(
                assignment.TerminalNumber,
                pair,
                expectedMarker,
                actualMarker,
                expectedDisplayMarker,
                actualDisplayMarker,
                ocrResult);
            candidates.Add(new ConfigurationComparisonCandidate(
                assignment,
                item,
                expectedMarker,
                actualMarker,
                actualDisplayMarker));
        }

        var items = ReconcileComparisonItemsWithConfiguration(candidates, configuredTerminals);

        var result = new ConfigurationComparisonResult
        {
            CabinetId = configuration.CabinetId,
            ResolvedTerminalStartNumber = alignment.ResolvedStartNumber,
            ResolvedTerminalEndNumber = alignment.ResolvedEndNumber,
            AlignmentStrategy = location.Status == "matched"
                ? $"{alignment.Strategy}-in-located-strip"
                : alignment.Strategy,
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
                string.Equals(result.TargetType, "wire-marker-tube", StringComparison.OrdinalIgnoreCase) ||
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
        string cabinetId,
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

    private static List<ConfigurationComparisonItem> ReconcileComparisonItemsWithConfiguration(
        IReadOnlyList<ConfigurationComparisonCandidate> candidates,
        IReadOnlyList<CabinetTerminalConfiguration> configuredTerminals)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var configurationOwnerByMarker = configuredTerminals
            .Select(terminal => new
            {
                terminal.TerminalNumber,
                Marker = NormalizeOcrText(terminal.ExpectedWireMarker)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Marker))
            .GroupBy(item => item.Marker!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.TerminalNumber).Distinct().Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.First().TerminalNumber,
                StringComparer.OrdinalIgnoreCase);
        var matchedOwnerByMarker = candidates
            .Where(candidate =>
                candidate.Item.Result == "matched" &&
                !string.IsNullOrWhiteSpace(candidate.ExpectedMarker) &&
                string.Equals(candidate.ExpectedMarker, candidate.ActualMarker, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => candidate.ExpectedMarker!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate.Assignment.TerminalNumber).First(),
                StringComparer.OrdinalIgnoreCase);
        var items = new List<ConfigurationComparisonItem>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (!TryResolveDuplicateConfigurationClaim(
                    candidate,
                    configurationOwnerByMarker,
                    matchedOwnerByMarker,
                    out var ownerTerminalNumber))
            {
                items.Add(candidate.Item);
                continue;
            }

            var ignoredMarker = candidate.ActualDisplayMarker ?? candidate.ActualMarker;
            var resolvedItem = string.IsNullOrWhiteSpace(candidate.ExpectedMarker)
                ? CreateConfigurationResolvedEmptyItem(candidate, ownerTerminalNumber, ignoredMarker)
                : CreateConfigurationSuppressedCandidateItem(candidate, ownerTerminalNumber, ignoredMarker);
            items.Add(resolvedItem);
        }

        return items;
    }

    private static bool TryResolveDuplicateConfigurationClaim(
        ConfigurationComparisonCandidate candidate,
        IReadOnlyDictionary<string, int> configurationOwnerByMarker,
        IReadOnlyDictionary<string, ConfigurationComparisonCandidate> matchedOwnerByMarker,
        out int ownerTerminalNumber)
    {
        ownerTerminalNumber = 0;

        if (string.IsNullOrWhiteSpace(candidate.ActualMarker) ||
            !configurationOwnerByMarker.TryGetValue(candidate.ActualMarker, out ownerTerminalNumber) ||
            ownerTerminalNumber == candidate.Assignment.TerminalNumber ||
            !matchedOwnerByMarker.TryGetValue(candidate.ActualMarker, out var ownerCandidate) ||
            ownerCandidate.Assignment.TerminalNumber != ownerTerminalNumber)
        {
            return false;
        }

        var sameWireDetection =
            candidate.Assignment.Pair.WireMarkerTube?.Id is int currentWireId &&
            ownerCandidate.Assignment.Pair.WireMarkerTube?.Id == currentWireId;
        var isWeakGeometryCandidate = candidate.Assignment.Pair.Category != "confirmed";

        return sameWireDetection || isWeakGeometryCandidate;
    }

    private static ConfigurationComparisonItem CreateConfigurationResolvedEmptyItem(
        ConfigurationComparisonCandidate candidate,
        int ownerTerminalNumber,
        string? ignoredMarker)
    {
        var markerText = string.IsNullOrWhiteSpace(ignoredMarker)
            ? "该几何候选线号管"
            : $"几何候选线号管 {ignoredMarker}";

        return CreateConfigurationReconciledItem(
            candidate,
            null,
            null,
            "configuration-empty-terminal",
            "ignored-duplicate",
            "matched",
            $"配置为空端子，{markerText} 已由端子 {ownerTerminalNumber} 的标准配置唯一命中，判定本端子为空端子。");
    }

    private static ConfigurationComparisonItem CreateConfigurationSuppressedCandidateItem(
        ConfigurationComparisonCandidate candidate,
        int ownerTerminalNumber,
        string? ignoredMarker)
    {
        var markerText = string.IsNullOrWhiteSpace(ignoredMarker)
            ? "该几何候选线号管"
            : $"几何候选线号管 {ignoredMarker}";

        return CreateConfigurationReconciledItem(
            candidate,
            candidate.Item.ExpectedWireMarker,
            null,
            "configuration-suppressed-candidate",
            "ignored-duplicate",
            "unrecognized",
            $"{markerText} 已由端子 {ownerTerminalNumber} 的标准配置唯一命中；本端子未获得自身配置线号管的有效识别。");
    }

    private static ConfigurationComparisonItem CreateConfigurationReconciledItem(
        ConfigurationComparisonCandidate candidate,
        string? expectedWireMarker,
        string? actualWireMarker,
        string pairCategory,
        string ocrStatus,
        string result,
        string message) =>
        new()
        {
            PairIndex = candidate.Item.PairIndex,
            TerminalNumber = candidate.Item.TerminalNumber,
            TerminalDetectionId = candidate.Item.TerminalDetectionId,
            WireMarkerTubeDetectionId = null,
            PairCategory = pairCategory,
            ExpectedWireMarker = expectedWireMarker,
            ActualWireMarker = actualWireMarker,
            Confidence = null,
            OcrStatus = ocrStatus,
            Result = result,
            Message = message
        };

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

    private static ConfigurationAlignment ResolveConfigurationAlignment(
        CabinetConfiguration configuration,
        IReadOnlyList<CabinetTerminalConfiguration> configuredTerminals,
        IReadOnlyList<DetectionPair> orderedPairs,
        IReadOnlyDictionary<int, OcrResult> ocrByDetectionId,
        IReadOnlySet<string> locationMarkers,
        bool useTerminalStartHint)
    {
        if (orderedPairs.Count == 0)
        {
            return new ConfigurationAlignment([], null, null, "no-detected-pairs");
        }

        if (configuredTerminals.Count == 0)
        {
            var fallbackAssignments = orderedPairs
                .Select((pair, index) =>
                    new ConfigurationPairAssignment(
                        pair,
                        configuration.TerminalStartNumber + index,
                        null))
                .ToArray();

            return new ConfigurationAlignment(
                fallbackAssignments,
                fallbackAssignments.First().TerminalNumber,
                fallbackAssignments.Last().TerminalNumber,
                "fallback-sequential-no-configuration");
        }

        if (configuredTerminals.Count < orderedPairs.Count)
        {
            var partialAssignments = orderedPairs
                .Select((pair, index) =>
                {
                    var terminal = index < configuredTerminals.Count ? configuredTerminals[index] : null;
                    return new ConfigurationPairAssignment(
                        pair,
                        terminal?.TerminalNumber ?? configuredTerminals[^1].TerminalNumber + index - configuredTerminals.Count + 1,
                        terminal);
                })
                .ToArray();

            return new ConfigurationAlignment(
                partialAssignments,
                partialAssignments.First().TerminalNumber,
                partialAssignments.Last().TerminalNumber,
                "partial-configuration-sequential");
        }

        var bestStartIndex = 0;
        var bestScore = double.NegativeInfinity;
        for (var startIndex = 0; startIndex <= configuredTerminals.Count - orderedPairs.Count; startIndex++)
        {
            var score = ScoreConfigurationWindow(
                configuration,
                configuredTerminals,
                orderedPairs,
                ocrByDetectionId,
                startIndex,
                locationMarkers,
                useTerminalStartHint);

            if (score > bestScore)
            {
                bestScore = score;
                bestStartIndex = startIndex;
            }
        }

        var assignments = orderedPairs
            .Select((pair, index) =>
            {
                var terminal = configuredTerminals[bestStartIndex + index];
                return new ConfigurationPairAssignment(pair, terminal.TerminalNumber, terminal);
            })
            .ToArray();

        return new ConfigurationAlignment(
            assignments,
            assignments.First().TerminalNumber,
            assignments.Last().TerminalNumber,
            "ocr-window-search");
    }

    private static double ScoreConfigurationWindow(
        CabinetConfiguration configuration,
        IReadOnlyList<CabinetTerminalConfiguration> configuredTerminals,
        IReadOnlyList<DetectionPair> orderedPairs,
        IReadOnlyDictionary<int, OcrResult> ocrByDetectionId,
        int startIndex,
        IReadOnlySet<string> locationMarkers,
        bool useTerminalStartHint)
    {
        var score = 0d;
        for (var pairIndex = 0; pairIndex < orderedPairs.Count; pairIndex++)
        {
            var terminal = configuredTerminals[startIndex + pairIndex];
            var pair = orderedPairs[pairIndex];
            var expectedMarker = NormalizeOcrText(terminal.ExpectedWireMarker);
            var actualMarker = GetRecognizedWireMarker(pair, ocrByDetectionId);

            score += ScoreMarkerMatch(expectedMarker, actualMarker);
            if (terminal.WireMarkers.Any(marker =>
                    ConfigurationMarkerNormalizer.Normalize(marker) is { } normalized &&
                    locationMarkers.Contains(normalized)))
            {
                score += 6d;
            }
        }

        if (useTerminalStartHint &&
            configuration.TerminalStartNumber > 0 &&
            configuredTerminals[startIndex].TerminalNumber == configuration.TerminalStartNumber)
        {
            score += 0.75d;
        }

        return score;
    }

    private static string? GetRecognizedWireMarker(
        DetectionPair pair,
        IReadOnlyDictionary<int, OcrResult> ocrByDetectionId)
    {
        if (pair.WireMarkerTube is null ||
            !ocrByDetectionId.TryGetValue(pair.WireMarkerTube.Id, out var ocrResult) ||
            ocrResult.Status != "recognized")
        {
            return null;
        }

        return NormalizeOcrText(ocrResult.NormalizedText ?? ocrResult.RawText);
    }

    private static double ScoreMarkerMatch(string? expectedMarker, string? actualMarker)
    {
        if (string.IsNullOrWhiteSpace(expectedMarker) && string.IsNullOrWhiteSpace(actualMarker))
        {
            return 1.0d;
        }

        if (string.IsNullOrWhiteSpace(expectedMarker))
        {
            return -2.0d;
        }

        if (string.IsNullOrWhiteSpace(actualMarker))
        {
            return 0d;
        }

        return string.Equals(expectedMarker, actualMarker, StringComparison.OrdinalIgnoreCase)
            ? 8.0d
            : -5.0d;
    }

    private async Task<CabinetConfiguration> LoadCabinetConfigurationAsync(
        string cabinetId,
        MqttVisionServerOptions options,
        CancellationToken cancellationToken)
    {
        var root = options.Processing.CabinetConfigurationRoot;
        var rootPath = Path.IsPathRooted(root)
            ? root
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, root));
        var fileName = string.IsNullOrWhiteSpace(cabinetId)
            ? "cabinet-dev.json"
            : $"{cabinetId}.json";
        var configPath = Path.Combine(rootPath, fileName);
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(rootPath, "cabinet-dev.json");
        }

        if (!File.Exists(configPath))
        {
            return new CabinetConfiguration
            {
                CabinetId = string.IsNullOrWhiteSpace(cabinetId) ? "cabinet-dev" : cabinetId,
                TerminalStartNumber = 1
            };
        }

        await using var stream = File.OpenRead(configPath);
        var configuration = await JsonSerializer.DeserializeAsync<CabinetConfiguration>(
            stream,
            JsonOptions,
            cancellationToken: cancellationToken);
        return configuration ?? new CabinetConfiguration
        {
            CabinetId = string.IsNullOrWhiteSpace(cabinetId) ? "cabinet-dev" : cabinetId,
            TerminalStartNumber = 1
        };
    }

    private static ConfigurationComparisonItem BuildConfigurationComparisonItem(
        int terminalNumber,
        DetectionPair pair,
        string? expectedMarker,
        string? actualMarker,
        string? expectedDisplayMarker,
        string? actualDisplayMarker,
        OcrResult? ocrResult)
    {
        if (string.IsNullOrWhiteSpace(expectedMarker))
        {
            if (string.IsNullOrWhiteSpace(actualMarker))
            {
                return CreateComparisonItem(
                    terminalNumber,
                    pair,
                    expectedDisplayMarker,
                    actualDisplayMarker,
                    ocrResult,
                    "matched",
                    "配置为空端子，检测结果未识别到有效线号管。");
            }

            return CreateComparisonItem(
                terminalNumber,
                pair,
                expectedDisplayMarker,
                actualDisplayMarker,
                ocrResult,
                "mismatch",
                "配置为空端子，但检测到有效线号管编号。");
        }

        if (string.IsNullOrWhiteSpace(actualMarker))
        {
            return CreateComparisonItem(
                terminalNumber,
                pair,
                expectedDisplayMarker,
                actualDisplayMarker,
                ocrResult,
                "unrecognized",
                "配置要求存在该线号管，但 OCR 未达到识别阈值或未找到规范标签。");
        }

        return string.Equals(expectedMarker, actualMarker, StringComparison.OrdinalIgnoreCase)
            ? CreateComparisonItem(
                terminalNumber,
                pair,
                expectedDisplayMarker,
                actualDisplayMarker,
                ocrResult,
                "matched",
                "线号管编号与测试配置一致。")
            : CreateComparisonItem(
                terminalNumber,
                pair,
                expectedDisplayMarker,
                actualDisplayMarker,
                ocrResult,
                "mismatch",
                "线号管编号与测试配置不一致。");
    }

    private static ConfigurationComparisonItem CreateComparisonItem(
        int terminalNumber,
        DetectionPair pair,
        string? expectedMarker,
        string? actualMarker,
        OcrResult? ocrResult,
        string result,
        string message) =>
        new()
        {
            PairIndex = pair.PairIndex,
            TerminalNumber = terminalNumber,
            TerminalDetectionId = pair.Terminal.Id,
            WireMarkerTubeDetectionId = pair.WireMarkerTube?.Id,
            PairCategory = pair.Category,
            ExpectedWireMarker = expectedMarker,
            ActualWireMarker = actualMarker,
            Confidence = ocrResult?.Confidence,
            OcrStatus = ocrResult?.Status ?? "missing",
            Result = result,
            Message = message
        };

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

    private sealed record ConfigurationAlignment(
        IReadOnlyList<ConfigurationPairAssignment> Assignments,
        int? ResolvedStartNumber,
        int? ResolvedEndNumber,
        string Strategy);

    private sealed record ConfigurationPairAssignment(
        DetectionPair Pair,
        int TerminalNumber,
        CabinetTerminalConfiguration? Configuration);

    private sealed record ConfigurationComparisonCandidate(
        ConfigurationPairAssignment Assignment,
        ConfigurationComparisonItem Item,
        string? ExpectedMarker,
        string? ActualMarker,
        string? ActualDisplayMarker);

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
