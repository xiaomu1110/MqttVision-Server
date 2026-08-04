using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MqttVision.Server.Infrastructure.Vision;

public sealed class YoloOnnxObjectDetector : IObjectDetector, IDisposable
{
    private const int TerminalClassId = 0;
    private const int WireMarkerTubeClassId = 1;

    private readonly ILogger<YoloOnnxObjectDetector> logger;
    private readonly IHostEnvironment environment;
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private ActiveYoloSession? activeSession;
    private bool disposed;

    public YoloOnnxObjectDetector(
        ILogger<YoloOnnxObjectDetector> logger,
        IHostEnvironment environment)
    {
        this.logger = logger;
        this.environment = environment;
    }

    public async Task<IReadOnlyList<DetectedObject>> DetectAsync(
        string imagePath,
        ProcessingOptions processing,
        CancellationToken cancellationToken)
    {
        var sessionSettings = YoloSessionSettings.From(processing, environment.ContentRootPath);
        if (string.IsNullOrWhiteSpace(sessionSettings.ModelPath))
        {
            throw new InvalidOperationException("未配置 YOLO ONNX 模型路径。");
        }

        if (!File.Exists(sessionSettings.ModelPath))
        {
            throw new FileNotFoundException("YOLO ONNX 模型文件不存在。", sessionSettings.ModelPath);
        }

        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var active = GetOrCreateSession(sessionSettings);
            var input = active.Session.InputMetadata.First();
            var inputName = input.Key;
            var inputShape = input.Value.Dimensions;
            var inputHeight = ResolveInputDimension(inputShape, 2, processing.YoloInputSize);
            var inputWidth = ResolveInputDimension(inputShape, 3, processing.YoloInputSize);

            var letterbox = await PreprocessAsync(imagePath, inputWidth, inputHeight, cancellationToken);
            var inputValue = NamedOnnxValue.CreateFromTensor(inputName, letterbox.Tensor);
            using var results = active.Session.Run(new[] { inputValue });

            var output = results.First().AsTensor<float>();
            var detections = ParseYoloOutput(output, letterbox, processing.ConfidenceThreshold);
            var kept = ApplyNms(detections, processing.NmsThreshold);

            for (var i = 0; i < kept.Count; i++)
            {
                kept[i].Id = i + 1;
            }

            logger.LogInformation(
                "YOLO detection completed. Image={Image}, Model={Model}, Candidates={Candidates}, Kept={Kept}",
                imagePath,
                sessionSettings.ModelPath,
                detections.Count,
                kept.Count);

            return kept;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public void Dispose()
    {
        sessionLock.Wait();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeSession?.Dispose();
            activeSession = null;
        }
        finally
        {
            sessionLock.Release();
            sessionLock.Dispose();
        }
    }

    private ActiveYoloSession GetOrCreateSession(YoloSessionSettings settings)
    {
        if (activeSession is not null && activeSession.Settings.Equals(settings))
        {
            return activeSession;
        }

        var nextSession = CreateSession(settings);
        var previousSession = activeSession;
        activeSession = nextSession;
        previousSession?.Dispose();
        return nextSession;
    }

    private ActiveYoloSession CreateSession(YoloSessionSettings settings)
    {
        var session = new InferenceSession(settings.ModelPath);
        try
        {
            var inputShape = string.Join("x", session.InputMetadata.First().Value.Dimensions);
            var outputShape = string.Join("x", session.OutputMetadata.First().Value.Dimensions);

            logger.LogInformation(
                "YOLO ONNX model loaded. Path={Path}, Input={Input}, Output={Output}",
                settings.ModelPath,
                inputShape,
                outputShape);

            return new ActiveYoloSession(settings, session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task<LetterboxImage> PreprocessAsync(
        string imagePath,
        int inputWidth,
        int inputHeight,
        CancellationToken cancellationToken)
    {
        using var source = await Image.LoadAsync<Rgb24>(imagePath, cancellationToken);
        var scale = Math.Min(inputWidth / (float)source.Width, inputHeight / (float)source.Height);
        var resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        var padX = (inputWidth - resizedWidth) / 2f;
        var padY = (inputHeight - resizedHeight) / 2f;

        using var resized = source.Clone(context => context.Resize(resizedWidth, resizedHeight));
        using var canvas = new Image<Rgb24>(inputWidth, inputHeight, new Rgb24(114, 114, 114));
        canvas.Mutate(context => context.DrawImage(resized, new Point((int)MathF.Round(padX), (int)MathF.Round(padY)), 1f));

        var tensor = new DenseTensor<float>(new[] { 1, 3, inputHeight, inputWidth });
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < inputHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < inputWidth; x++)
                {
                    tensor[0, 0, y, x] = row[x].R / 255f;
                    tensor[0, 1, y, x] = row[x].G / 255f;
                    tensor[0, 2, y, x] = row[x].B / 255f;
                }
            }
        });

        return new LetterboxImage(
            tensor,
            source.Width,
            source.Height,
            inputWidth,
            inputHeight,
            scale,
            padX,
            padY);
    }

    private List<DetectedObject> ParseYoloOutput(
        Tensor<float> output,
        LetterboxImage letterbox,
        float confidenceThreshold)
    {
        var dimensions = output.Dimensions.ToArray();
        var values = output.ToArray();
        var layout = ResolveOutputLayout(dimensions);
        var candidates = new List<DetectedObject>();

        for (var boxIndex = 0; boxIndex < layout.BoxCount; boxIndex++)
        {
            var classId = -1;
            var confidence = 0f;

            for (var classOffset = 0; classOffset < layout.ClassCount; classOffset++)
            {
                var score = layout.Get(values, boxIndex, 4 + classOffset);
                if (score > confidence)
                {
                    confidence = score;
                    classId = classOffset;
                }
            }

            if (confidence < confidenceThreshold ||
                (classId != TerminalClassId && classId != WireMarkerTubeClassId))
            {
                continue;
            }

            var centerX = layout.Get(values, boxIndex, 0);
            var centerY = layout.Get(values, boxIndex, 1);
            var width = layout.Get(values, boxIndex, 2);
            var height = layout.Get(values, boxIndex, 3);
            var left = (centerX - width / 2 - letterbox.PadX) / letterbox.Scale;
            var top = (centerY - height / 2 - letterbox.PadY) / letterbox.Scale;
            var right = (centerX + width / 2 - letterbox.PadX) / letterbox.Scale;
            var bottom = (centerY + height / 2 - letterbox.PadY) / letterbox.Scale;

            left = Math.Clamp(left, 0, letterbox.OriginalWidth);
            top = Math.Clamp(top, 0, letterbox.OriginalHeight);
            right = Math.Clamp(right, 0, letterbox.OriginalWidth);
            bottom = Math.Clamp(bottom, 0, letterbox.OriginalHeight);

            if (right - left < 1 || bottom - top < 1)
            {
                continue;
            }

            candidates.Add(new DetectedObject
            {
                ClassId = classId,
                ClassName = GetClassName(classId),
                Confidence = confidence,
                Box = new DetectionBox(left, top, right - left, bottom - top)
            });
        }

        return candidates;
    }

    private static List<DetectedObject> ApplyNms(
        IReadOnlyCollection<DetectedObject> candidates,
        float nmsThreshold)
    {
        var kept = new List<DetectedObject>();

        foreach (var group in candidates.GroupBy(candidate => candidate.ClassId))
        {
            var ordered = group
                .OrderByDescending(candidate => candidate.Confidence)
                .ToList();

            while (ordered.Count > 0)
            {
                var best = ordered[0];
                kept.Add(best);
                ordered.RemoveAt(0);
                ordered = ordered
                    .Where(candidate => ComputeIou(best.Box, candidate.Box) <= nmsThreshold)
                    .ToList();
            }
        }

        return kept
            .OrderBy(candidate => candidate.ClassId)
            .ThenBy(candidate => candidate.Box.X)
            .ThenBy(candidate => candidate.Box.Y)
            .ToList();
    }

    private static float ComputeIou(DetectionBox first, DetectionBox second)
    {
        var x1 = Math.Max(first.X, second.X);
        var y1 = Math.Max(first.Y, second.Y);
        var x2 = Math.Min(first.Right, second.Right);
        var y2 = Math.Min(first.Bottom, second.Bottom);
        var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = first.Width * first.Height + second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static int ResolveInputDimension(
        IReadOnlyList<int> dimensions,
        int index,
        int configuredSize)
    {
        if (dimensions.Count > index && dimensions[index] > 0)
        {
            return dimensions[index];
        }

        return configuredSize;
    }

    private static YoloOutputLayout ResolveOutputLayout(int[] dimensions)
    {
        if (dimensions.Length == 3)
        {
            var first = dimensions[1];
            var second = dimensions[2];
            if (first <= second && first <= 128)
            {
                return YoloOutputLayout.ChannelsFirst(first, second);
            }

            return YoloOutputLayout.ChannelsLast(second, first);
        }

        if (dimensions.Length == 2)
        {
            return YoloOutputLayout.ChannelsLast(dimensions[1], dimensions[0]);
        }

        throw new NotSupportedException($"不支持的 YOLO 输出维度: {string.Join("x", dimensions)}");
    }

    private static string GetClassName(int classId) =>
        classId switch
        {
            TerminalClassId => "Terminal",
            WireMarkerTubeClassId => "WireMarkerTube",
            _ => $"Class{classId}"
        };

    private sealed record LetterboxImage(
        DenseTensor<float> Tensor,
        int OriginalWidth,
        int OriginalHeight,
        int InputWidth,
        int InputHeight,
        float Scale,
        float PadX,
        float PadY);

    private sealed record YoloSessionSettings(string ModelPath)
    {
        public static YoloSessionSettings From(ProcessingOptions processing, string contentRootPath) =>
            new(NormalizeModelPath(processing.YoloOnnxModelPath, contentRootPath));

        private static string NormalizeModelPath(string modelPath, string contentRootPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return string.Empty;
            }

            var trimmed = modelPath.Trim();
            return Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(contentRootPath, trimmed));
        }
    }

    private sealed class ActiveYoloSession(
        YoloSessionSettings settings,
        InferenceSession session) : IDisposable
    {
        public YoloSessionSettings Settings { get; } = settings;

        public InferenceSession Session { get; } = session;

        public void Dispose()
        {
            Session.Dispose();
        }
    }

    private sealed class YoloOutputLayout
    {
        private readonly bool channelsFirst;

        private YoloOutputLayout(int attributeCount, int boxCount, bool channelsFirst)
        {
            AttributeCount = attributeCount;
            BoxCount = boxCount;
            ClassCount = Math.Max(0, attributeCount - 4);
            this.channelsFirst = channelsFirst;
        }

        public int AttributeCount { get; }

        public int BoxCount { get; }

        public int ClassCount { get; }

        public static YoloOutputLayout ChannelsFirst(int attributeCount, int boxCount) =>
            new(attributeCount, boxCount, true);

        public static YoloOutputLayout ChannelsLast(int attributeCount, int boxCount) =>
            new(attributeCount, boxCount, false);

        public float Get(float[] values, int boxIndex, int attributeIndex)
        {
            if (attributeIndex >= AttributeCount)
            {
                return 0;
            }

            return channelsFirst
                ? values[attributeIndex * BoxCount + boxIndex]
                : values[boxIndex * AttributeCount + attributeIndex];
        }
    }
}
