using MqttVision.Server.Contracts;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Api;

public static class OpsEndpoints
{
    public static WebApplication MapOpsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/ops");

        api.MapGet("/summary", async (
            OpsStateService ops,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await ops.GetSnapshotAsync(BuildRequestBaseUrl(request), cancellationToken);
            return Results.Ok(ApiResponse<OpsDashboardSnapshot>.Ok(snapshot, "运维总览读取完成。"));
        });

        api.MapGet("/health", async (
            OpsStateService ops,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await ops.GetSnapshotAsync(BuildRequestBaseUrl(request), cancellationToken);
            return Results.Ok(new
            {
                status = "ok",
                time = snapshot.ServerTime,
                mqttSubscriber = snapshot.MqttSubscriber,
                mqttPublisher = snapshot.MqttPublisher,
                ocrService = snapshot.OcrService,
                yoloModel = snapshot.YoloModel
            });
        });

        return app;
    }

    private static string BuildRequestBaseUrl(HttpRequest request)
    {
        var scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
            ? forwardedProto.FirstOrDefault() ?? request.Scheme
            : request.Scheme;
        var host = request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost)
            ? forwardedHost.FirstOrDefault() ?? request.Host.Value
            : request.Host.Value;
        var pathBase = request.PathBase.Value?.TrimEnd('/') ?? string.Empty;

        return $"{scheme}://{host}{pathBase}";
    }
}
