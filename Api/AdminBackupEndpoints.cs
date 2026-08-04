using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminBackupEndpoints
{
    public static WebApplication MapAdminBackupEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/backups")
            .RequireAuthorization();

        api.MapGet("/", async (
            AdminConfigurationBackupService backups,
            CancellationToken cancellationToken) =>
        {
            var summaries = await backups.ListAsync(cancellationToken);
            return Results.Ok(ApiResponse<IReadOnlyList<AdminConfigurationBackupSummary>>.Ok(
                summaries,
                "备份列表读取完成。"));
        });

        api.MapPost("/", async (
            AdminConfigurationBackupCreateRequest request,
            AdminConfigurationBackupService backups,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await backups.CreateAsync(request, cancellationToken);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Backup,
                "创建配置备份",
                result.Success ? AdminAuditOutcomes.Success : AdminAuditOutcomes.Failure,
                result.Message,
                result.Backup?.BackupId,
                new Dictionary<string, string?>
                {
                    ["包含系统配置"] = request.IncludeRuntime ? "是" : "否",
                    ["包含柜体配置"] = request.IncludeCabinets ? "是" : "否",
                    ["柜体数量"] = result.Backup?.CabinetCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                cancellationToken);
            return result.Success
                ? Results.Ok(ApiResponse<AdminConfigurationBackupCreateResult>.Ok(result, result.Message))
                : Results.BadRequest(ApiResponse<AdminConfigurationBackupCreateResult>.Fail(result.Message));
        });

        api.MapPost("/{backupId}/validate", async (
            string backupId,
            AdminConfigurationBackupRestoreRequest request,
            AdminConfigurationBackupService backups,
            CancellationToken cancellationToken) =>
        {
            var plan = await backups.GetRestorePlanAsync(backupId, request, cancellationToken);
            return Results.Ok(ApiResponse<AdminConfigurationBackupRestorePlan>.Ok(
                plan,
                plan.CanRestore ? "备份预检通过。" : "备份预检未通过。"));
        });

        api.MapPost("/{backupId}/restore", async (
            string backupId,
            AdminConfigurationBackupRestoreRequest request,
            AdminConfigurationBackupService backups,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await backups.RestoreAsync(backupId, request, cancellationToken);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Backup,
                "恢复配置备份",
                result.Success ? AdminAuditOutcomes.Success : AdminAuditOutcomes.Failure,
                result.Message,
                backupId,
                new Dictionary<string, string?>
                {
                    ["恢复系统配置"] = request.IncludeRuntime ? "是" : "否",
                    ["恢复柜体配置"] = request.IncludeCabinets ? "是" : "否",
                    ["安全备份"] = result.SafetyBackup?.BackupId,
                    ["柜体数量"] = result.Plan.CabinetCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                cancellationToken);
            return result.Success
                ? Results.Ok(ApiResponse<AdminConfigurationBackupRestoreResult>.Ok(result, result.Message))
                : Results.BadRequest(ApiResponse<AdminConfigurationBackupRestoreResult>.Fail(result.Message));
        });

        api.MapGet("/{backupId}/download", async (
            string backupId,
            AdminConfigurationBackupService backups,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var bytes = await backups.ReadBackupBytesAsync(backupId, cancellationToken);
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Backup,
                    "下载配置备份",
                    AdminAuditOutcomes.Success,
                    "管理员下载了配置备份文件。",
                    backupId,
                    new Dictionary<string, string?>
                    {
                        ["文件大小"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
                    cancellationToken);
                return Results.File(
                    bytes,
                    "application/json",
                    $"{backupId}.json");
            }
            catch (InvalidOperationException ex)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Backup,
                    "下载配置备份",
                    AdminAuditOutcomes.Failure,
                    ex.Message,
                    backupId,
                    cancellationToken: cancellationToken);
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        });

        return app;
    }
}
