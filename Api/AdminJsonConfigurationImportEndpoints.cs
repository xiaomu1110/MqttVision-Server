using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Api;

public static class AdminJsonConfigurationImportEndpoints
{
    public static WebApplication MapAdminJsonConfigurationImportEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/json-config-imports")
            .RequireAuthorization();

        api.MapGet("/", (JsonConfigurationImportService imports) =>
            Results.Ok(ApiResponse<IReadOnlyList<ConfigurationImportBatchRecord>>.Ok(
                imports.ListBatches(),
                "JSON 配置导入批次读取完成。")));

        api.MapGet("/{batchId}", (string batchId, JsonConfigurationImportService imports) =>
        {
            var batch = imports.GetBatch(batchId);
            return batch is null
                ? Results.NotFound(ApiResponse<object>.Fail("未找到指定 JSON 配置导入批次。"))
                : Results.Ok(ApiResponse<ConfigurationImportBatchRecord>.Ok(batch, "JSON 配置导入批次读取完成。"));
        });

        api.MapPost("/", async (
            HttpRequest request,
            JsonConfigurationImportService imports,
            RuntimeConfigurationService configuration,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("请使用 multipart/form-data 上传 JSON 配置文件。"));
            }

            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("至少需要选择一个 JSON 配置文件。"));
            }

            try
            {
                var uploads = form.Files.Select(file => new ConfigurationImportUpload(
                    file.FileName,
                    file.Length,
                    file.ContentType,
                    _ => Task.FromResult<Stream>(file.OpenReadStream()))).ToArray();
                var batch = await imports.CreateBatchAsync(uploads, cancellationToken);
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.JsonConfigurationImport,
                    "创建 JSON 配置批量导入",
                    AdminAuditOutcomes.Success,
                    $"已接收 {batch.TotalFiles} 个 JSON 配置文件。",
                    batch.BatchId,
                    new Dictionary<string, string?>
                    {
                        ["文件数量"] = batch.TotalFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["最大并发"] = Math.Clamp(configuration.Current.JsonImport.MaxConcurrentImports, 1, 3)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
                    cancellationToken);
                return Results.Accepted($"/api/admin/json-config-imports/{batch.BatchId}",
                    ApiResponse<ConfigurationImportBatchRecord>.Ok(batch, "JSON 配置文件已接收，导入任务已排队。"));
            }
            catch (InvalidOperationException ex)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.JsonConfigurationImport,
                    "创建 JSON 配置批量导入",
                    AdminAuditOutcomes.Failure,
                    ex.Message,
                    null,
                    null,
                    cancellationToken);
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        }).DisableAntiforgery();

        return app;
    }
}
