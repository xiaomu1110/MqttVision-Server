using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Api;

public static class AdminCadImportEndpoints
{
    public static WebApplication MapAdminCadImportEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/cad-imports")
            .RequireAuthorization();

        api.MapGet("/", (CadImportService imports) =>
            Results.Ok(ApiResponse<IReadOnlyList<CadImportBatchRecord>>.Ok(
                imports.ListBatches(),
                "CAD 导入批次读取完成。")));

        api.MapGet("/{batchId}", (string batchId, CadImportService imports) =>
        {
            var batch = imports.GetBatch(batchId);
            return batch is null
                ? Results.NotFound(ApiResponse<object>.Fail("未找到指定 CAD 导入批次。"))
                : Results.Ok(ApiResponse<CadImportBatchRecord>.Ok(batch, "CAD 导入批次读取完成。"));
        });

        api.MapPost("/", async (
            HttpRequest request,
            CadImportService imports,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("请使用 multipart/form-data 上传 CAD 文件。"));
            }

            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("至少需要选择一个 DWG 或 DXF 文件。"));
            }

            try
            {
                var uploads = form.Files.Select(file => new CadImportUpload(
                    file.FileName,
                    file.Length,
                    file.ContentType,
                    _ => Task.FromResult<Stream>(file.OpenReadStream()))).ToArray();
                var batch = await imports.CreateBatchAsync(uploads, cancellationToken);
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.CadImport,
                    "创建 CAD 批量导入",
                    AdminAuditOutcomes.Success,
                    $"已接收 {batch.TotalFiles} 个 CAD 文件。",
                    batch.BatchId,
                    new Dictionary<string, string?>
                    {
                        ["文件数量"] = batch.TotalFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["最大并发"] = "3"
                    },
                    cancellationToken);
                return Results.Accepted($"/api/admin/cad-imports/{batch.BatchId}",
                    ApiResponse<CadImportBatchRecord>.Ok(batch, "CAD 文件已接收，解析任务已排队。"));
            }
            catch (InvalidOperationException ex)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.CadImport,
                    "创建 CAD 批量导入",
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
