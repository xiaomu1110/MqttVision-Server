using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public interface ITextRecognizer
{
    Task<TextRecognitionResult> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken);
}

public sealed record TextRecognitionResult(
    string Status,
    string? Text,
    double? Confidence,
    string? ErrorMessage)
{
    public static TextRecognitionResult Skipped(string reason) =>
        new("skipped", null, null, reason);

    public static TextRecognitionResult Recognized(string text, double? confidence) =>
        new("recognized", text, confidence, null);

    public static TextRecognitionResult Unrecognized(string? text, double? confidence, string reason) =>
        new("unrecognized", text, confidence, reason);

    public static TextRecognitionResult NoText() =>
        new("no-text", null, null, null);

    public static TextRecognitionResult Failed(string errorMessage) =>
        new("failed", null, null, errorMessage);
}
