using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminMaintenanceEndpoints
{
    public static WebApplication MapAdminMaintenanceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/maintenance")
            .RequireAuthorization();

        api.MapGet("/", async (
            AdminMaintenanceService maintenance,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await maintenance.GetSnapshotAsync(cancellationToken);
            return Results.Ok(ApiResponse<AdminMaintenanceSnapshot>.Ok(
                snapshot,
                "运行维护信息读取完成。"));
        });

        api.MapPost("/cleanup", async (
            AdminMaintenanceCleanupRequest request,
            AdminMaintenanceService maintenance,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await maintenance.CleanupAsync(request, cancellationToken);
            await audit.RecordHttpAsync(
                context,
                "运行维护",
                request.DryRun ? "预览清理" : "执行清理",
                result.Success ? AdminAuditOutcomes.Success : AdminAuditOutcomes.Failure,
                result.Message,
                "运行数据",
                new Dictionary<string, string?>
                {
                    ["保留天数"] = request.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["候选数量"] = result.CandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["删除数量"] = result.DeletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                cancellationToken);
            return result.Success
                ? Results.Ok(ApiResponse<AdminMaintenanceCleanupResult>.Ok(result, result.Message))
                : Results.BadRequest(ApiResponse<AdminMaintenanceCleanupResult>.Fail(result.Message));
        });

        return app;
    }
}
