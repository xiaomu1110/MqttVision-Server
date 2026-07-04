namespace MqttVision.Server.Contracts;

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string Message)
{
    public static ApiResponse<T> Ok(T data, string message = "ok") =>
        new(true, data, message);

    public static ApiResponse<T> Fail(string message) =>
        new(false, default, message);
}
