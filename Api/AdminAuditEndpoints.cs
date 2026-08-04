using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminAuditEndpoints
{
    public static WebApplication MapAdminAuditEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/audit")
            .RequireAuthorization();

        api.MapGet("/", async (
            string? category,
            string? outcome,
            string? action,
            int? limit,
            AdminAuditService audit,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await audit.GetSnapshotAsync(
                new AdminAuditQuery
                {
                    Category = category,
                    Outcome = outcome,
                    Action = action,
                    Limit = limit ?? 100
                },
                cancellationToken);
            return Results.Ok(ApiResponse<AdminAuditSnapshot>.Ok(
                snapshot,
                "操作审计读取完成。"));
        });

        return app;
    }
}
