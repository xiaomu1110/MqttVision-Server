using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Infrastructure.Vision;

public sealed class PaddleOcrServingTextRecognizer : ITextRecognizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly ILogger<PaddleOcrServingTextRecognizer> logger;
    private readonly MqttVisionServerOptions options;
    private readonly OpsStateService ops;

    public PaddleOcrServingTextRecognizer(
        HttpClient httpClient,
        ILogger<PaddleOcrServingTextRecognizer> logger,
        IOptions<MqttVisionServerOptions> options,
        OpsStateService ops)
    {
        this.httpClient = httpClient;
        this.logger = logger;
        this.options = options.Value;
        this.ops = ops;
    }

    public async Task<TextRecognitionResult> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var processing = options.Processing;
        if (!processing.PaddleOcrEnabled)
        {
            ops.RecordOcrState("disabled", "PaddleOCR serving is disabled by configuration.");
            return TextRecognitionResult.Skipped("PaddleOCR serving is disabled by configuration.");
        }

        if (string.IsNullOrWhiteSpace(processing.PaddleOcrServiceUrl))
        {
            ops.RecordOcrState("error", "PaddleOCR serving endpoint is not configured.");
            return TextRecognitionResult.Skipped("PaddleOCR serving endpoint is not configured.");
        }

        if (!File.Exists(imagePath))
        {
            return TextRecognitionResult.Failed($"OCR image file not found: {imagePath}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, processing.PaddleOcrTimeoutSeconds)));

        try
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath, timeoutCts.Token);
            var requestBody = BuildRequestBody(
                Convert.ToBase64String(imageBytes),
                processing);
            using var request = new HttpRequestMessage(HttpMethod.Post, processing.PaddleOcrServiceUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestBody, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                ops.RecordOcrState("error", $"HTTP {(int)response.StatusCode}: {TrimForLog(responseBody)}");
                return TextRecognitionResult.Failed(
                    $"PaddleOCR serving returned HTTP {(int)response.StatusCode}: {TrimForLog(responseBody)}");
            }

            var result = ParseServingResponse(
                responseBody,
                processing.PaddleOcrMinimumTextScore);
            ops.RecordOcrState("online", $"Endpoint={processing.PaddleOcrServiceUrl}, LastStatus={result.Status}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ops.RecordOcrState("error", $"PaddleOCR serving timed out after {Math.Max(1, processing.PaddleOcrTimeoutSeconds)} seconds.");
            return TextRecognitionResult.Failed(
                $"PaddleOCR serving timed out after {Math.Max(1, processing.PaddleOcrTimeoutSeconds)} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            logger.LogWarning(
                ex,
                "PaddleOCR serving request failed. Image={ImagePath}, Endpoint={Endpoint}",
                imagePath,
                processing.PaddleOcrServiceUrl);
            ops.RecordOcrState("error", ex.Message);
            return TextRecognitionResult.Failed(ex.Message);
        }
    }

    private static object BuildRequestBody(
        string imageBase64,
        ProcessingOptions processing)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file"] = imageBase64,
            ["fileType"] = processing.PaddleOcrFileType,
            ["visualize"] = processing.PaddleOcrVisualize
        };
        AddOptional(payload, "useDocOrientationClassify", processing.PaddleOcrUseDocOrientationClassify);
        AddOptional(payload, "useDocUnwarping", processing.PaddleOcrUseDocUnwarping);
        AddOptional(payload, "useTextlineOrientation", processing.PaddleOcrUseTextlineOrientation);

        return IsHighStabilityMode(processing.PaddleOcrDeploymentMode)
            ? new
            {
                inputs = new[]
                {
                    new
                    {
                        name = "input",
                        shape = new[] { 1, 1 },
                        datatype = "BYTES",
                        data = new[] { JsonSerializer.Serialize(payload, JsonOptions) }
                    }
                },
                outputs = new[]
                {
                    new { name = "output" }
                }
            }
            : payload;
    }

    private static void AddOptional(
        IDictionary<string, object?> payload,
        string key,
        bool? value)
    {
        if (value.HasValue)
        {
            payload[key] = value.Value;
        }
    }

    private static bool IsHighStabilityMode(string mode) =>
        mode.Contains("high", StringComparison.OrdinalIgnoreCase) ||
        mode.Contains("triton", StringComparison.OrdinalIgnoreCase) ||
        mode.Contains("hps", StringComparison.OrdinalIgnoreCase);

    // internal 以便单元测试直接验证 basic-serving / high-stability 两种响应解析逻辑,
    // 无需起 HTTP 服务。调用方仅为本类与测试项目。
    internal static TextRecognitionResult ParseServingResponse(
        string responseBody,
        double minimumTextScore)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (TryReadHighStabilityPayload(document.RootElement, out var innerJson))
        {
            using var innerDocument = JsonDocument.Parse(innerJson);
            return ParsePaddleOcrPayload(innerDocument.RootElement, minimumTextScore);
        }

        return ParsePaddleOcrPayload(document.RootElement, minimumTextScore);
    }

    private static bool TryReadHighStabilityPayload(
        JsonElement root,
        out string innerJson)
    {
        innerJson = string.Empty;
        if (!root.TryGetProperty("outputs", out var outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var output in outputs.EnumerateArray())
        {
            if (!output.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var first = data.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.String)
            {
                innerJson = first.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(innerJson);
            }
        }

        return false;
    }

    private static TextRecognitionResult ParsePaddleOcrPayload(
        JsonElement root,
        double minimumTextScore)
    {
        if (ReadInt(root, "errorCode") is { } errorCode && errorCode != 0)
        {
            return TextRecognitionResult.Failed(
                ReadString(root, "errorMsg") ??
                ReadString(root, "message") ??
                $"PaddleOCR serving errorCode={errorCode}.");
        }

        var parseRoot = TryGetObject(root, "result", out var result)
            ? result
            : root;
        if (TryGetArray(parseRoot, "ocrResults", out var ocrResults))
        {
            return ParseOcrResultArray(ocrResults, minimumTextScore);
        }

        var selectedCandidate = SelectBestTextCandidate(
            ReadOcrTextCandidates(parseRoot),
            minimumTextScore);
        if (selectedCandidate.Status != "no-text")
        {
            return selectedCandidate;
        }

        var text = NormalizeCandidateText(ReadOcrText(parseRoot, minimumTextScore));
        var confidence = ReadOcrConfidence(parseRoot, minimumTextScore);
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextRecognitionResult.NoText();
        }

        if (!IsCanonicalWireMarkerText(text))
        {
            return TextRecognitionResult.Unrecognized(
                text,
                confidence,
                "OCR result does not contain a canonical wire-marker token with '-' or '/'.");
        }

        if (!confidence.HasValue || confidence.Value < minimumTextScore)
        {
            return TextRecognitionResult.Unrecognized(
                text,
                confidence,
                $"OCR score is below threshold {minimumTextScore:0.00}.");
        }

        return TextRecognitionResult.Recognized(text, confidence);
    }

    private static TextRecognitionResult ParseOcrResultArray(
        JsonElement ocrResults,
        double minimumTextScore)
    {
        var candidates = new List<OcrTextCandidate>();
        foreach (var item in ocrResults.EnumerateArray())
        {
            var resultItem = TryGetObject(item, "prunedResult", out var prunedResult)
                ? prunedResult
                : item;
            candidates.AddRange(ReadOcrTextCandidates(resultItem));
        }

        return SelectBestTextCandidate(candidates, minimumTextScore);
    }

    private static TextRecognitionResult SelectBestTextCandidate(
        IReadOnlyCollection<OcrTextCandidate> candidates,
        double minimumTextScore)
    {
        var nonEmptyCandidates = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToArray();
        if (nonEmptyCandidates.Length == 0)
        {
            return TextRecognitionResult.NoText();
        }

        var bestCandidate = nonEmptyCandidates
            .OrderByDescending(candidate => IsCanonicalWireMarkerText(candidate.Text))
            .ThenByDescending(candidate => candidate.Score ?? 0)
            .ThenByDescending(candidate => candidate.Text?.Length ?? 0)
            .First();
        var bestText = bestCandidate.Text ?? string.Empty;
        if (!IsCanonicalWireMarkerText(bestText))
        {
            return TextRecognitionResult.Unrecognized(
                bestText,
                bestCandidate.Score,
                "OCR result does not contain a canonical wire-marker token with '-' or '/'.");
        }

        if (!bestCandidate.Score.HasValue || bestCandidate.Score.Value < minimumTextScore)
        {
            return TextRecognitionResult.Unrecognized(
                bestText,
                bestCandidate.Score,
                $"OCR score is below threshold {minimumTextScore:0.00}.");
        }

        return TextRecognitionResult.Recognized(bestText, bestCandidate.Score);
    }

    private static string? ReadOcrText(JsonElement root, double minimumTextScore)
    {
        if (TryGetObject(root, "res", out var res))
        {
            return ReadOcrText(res, minimumTextScore);
        }

        if (TryGetObject(root, "prunedResult", out var prunedResult))
        {
            return ReadOcrText(prunedResult, minimumTextScore);
        }

        var directText = ReadString(root, "text") ??
            ReadString(root, "Text") ??
            ReadString(root, "rec_text") ??
            ReadString(root, "recText") ??
            ReadString(root, "recognizedText");
        if (!string.IsNullOrWhiteSpace(directText))
        {
            return directText;
        }

        return SelectBestTextCandidate(ReadOcrTextCandidates(root), minimumTextScore).Text ??
            ReadStringArray(root, "texts");
    }

    private static double? ReadOcrConfidence(JsonElement root, double minimumTextScore)
    {
        if (TryGetObject(root, "res", out var res))
        {
            return ReadOcrConfidence(res, minimumTextScore);
        }

        if (TryGetObject(root, "prunedResult", out var prunedResult))
        {
            return ReadOcrConfidence(prunedResult, minimumTextScore);
        }

        return ReadDouble(root, "confidence") ??
            ReadDouble(root, "Confidence") ??
            ReadDouble(root, "score") ??
            ReadDouble(root, "rec_score") ??
            ReadDouble(root, "recScore") ??
            ReadDoubleArrayAverage(root, "rec_scores", minimumTextScore) ??
            ReadDoubleArrayAverage(root, "recScores", minimumTextScore) ??
            ReadDoubleArrayAverage(root, "scores");
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement property) =>
        root.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement root, string propertyName, out JsonElement property) =>
        root.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.Array;

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!TryGetArray(root, propertyName, out var property))
        {
            return null;
        }

        var values = property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return values.Length == 0 ? null : string.Join(" ", values);
    }

    private static IReadOnlyList<OcrTextCandidate> ReadOcrTextCandidates(JsonElement root)
    {
        var candidates = new List<OcrTextCandidate>();
        candidates.AddRange(ReadScoredTextArray(root, "rec_texts", "rec_scores"));
        candidates.AddRange(ReadScoredTextArray(root, "recTexts", "recScores"));
        if (candidates.Count == 0)
        {
            candidates.AddRange(ReadUnscoredTextArray(root, "texts"));
        }

        return candidates;
    }

    private static IReadOnlyList<OcrTextCandidate> ReadScoredTextArray(
        JsonElement root,
        string textPropertyName,
        string scorePropertyName)
    {
        if (!TryGetArray(root, textPropertyName, out var texts))
        {
            return Array.Empty<OcrTextCandidate>();
        }

        var textValues = texts
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        var scoreValues = ReadDoubleArray(root, scorePropertyName);
        return textValues
            .Select((text, index) => new
                OcrTextCandidate(
                    NormalizeCandidateText(text),
                    index < scoreValues.Length ? scoreValues[index] : null))
            .ToArray();
    }

    private static IReadOnlyList<OcrTextCandidate> ReadUnscoredTextArray(
        JsonElement root,
        string textPropertyName)
    {
        if (!TryGetArray(root, textPropertyName, out var texts))
        {
            return Array.Empty<OcrTextCandidate>();
        }

        return texts
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? new OcrTextCandidate(NormalizeCandidateText(item.GetString()), null)
                : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static string? NormalizeCandidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return CollapseDuplicateSlashes(string.Concat(text
            .Trim()
            .Select(character => character switch
            {
                '／' => '/',
                '–' or '—' or '－' => '-',
                '＇' or '\'' or '’' or '‘' => '/',
                _ => character
            })
            .Where(character => !char.IsWhiteSpace(character))
        )).ToUpperInvariant();
    }

    private static bool IsCanonicalWireMarkerText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains('-', StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal));

    private static string CollapseDuplicateSlashes(string value)
    {
        var builder = new StringBuilder(value.Length);
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

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
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
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
    }

    private static double? ReadDoubleArrayAverage(
        JsonElement root,
        string propertyName,
        double? minimumValue = null)
    {
        var values = ReadDoubleArray(root, propertyName)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Where(value => !minimumValue.HasValue || value >= minimumValue.Value)
            .ToArray();

        return values.Length == 0 ? null : values.Average();
    }

    private static double?[] ReadDoubleArray(JsonElement root, string propertyName)
    {
        if (!TryGetArray(root, propertyName, out var property))
        {
            return Array.Empty<double?>();
        }

        return property
            .EnumerateArray()
            .Select(item => item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetDouble(out var value) => (double?)value,
                JsonValueKind.String when double.TryParse(
                    item.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var stringValue) => (double?)stringValue,
                _ => (double?)null
            })
            .ToArray();
    }

    private static string TrimForLog(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private sealed record OcrTextCandidate(string? Text, double? Score);
}
