using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminYoloModelEndpoints
{
    public static WebApplication MapAdminYoloModelEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/yolo-models")
            .RequireAuthorization();

        api.MapGet("/", (
            YoloModelFileService models,
            RuntimeConfigurationService configuration) =>
            Results.Ok(ApiResponse<IReadOnlyList<YoloModelFileDescriptor>>.Ok(
                models.List(configuration.Current.Processing.YoloOnnxModelPath),
                "目标检测模型列表读取完成。")));

        api.MapPost("/", async (
            HttpRequest request,
            YoloModelFileService models,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("请使用 multipart/form-data 上传 .onnx 模型文件。"));
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("model") ??
                (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null)
            {
                return Results.BadRequest(ApiResponse<object>.Fail("缺少模型文件字段 model。"));
            }

            try
            {
                var uploaded = await models.UploadAsync(
                    file.FileName,
                    file.Length,
                    _ => Task.FromResult<Stream>(file.OpenReadStream()),
                    cancellationToken);
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.RuntimeConfiguration,
                    "上传目标检测模型",
                    AdminAuditOutcomes.Success,
                    $"已上传目标检测模型 {uploaded.FileName}。",
                    uploaded.RelativePath,
                    new Dictionary<string, string?>
                    {
                        ["文件大小"] = uploaded.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["来源"] = "后台接口"
                    },
                    cancellationToken);
                return Results.Ok(ApiResponse<YoloModelFileDescriptor>.Ok(uploaded, "目标检测模型上传完成，请在系统配置中选择并保存。"));
            }
            catch (InvalidOperationException ex)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.RuntimeConfiguration,
                    "上传目标检测模型",
                    AdminAuditOutcomes.Failure,
                    ex.Message,
                    file.FileName,
                    new Dictionary<string, string?>
                    {
                        ["来源"] = "后台接口"
                    },
                    cancellationToken);
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        })
        .DisableAntiforgery();

        return app;
    }
}
