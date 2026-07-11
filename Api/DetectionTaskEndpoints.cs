using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Infrastructure.Storage;
using System.Text.Json.Nodes;

namespace MqttVision.Server.Api;

public static class DetectionTaskEndpoints
{
    public static WebApplication MapDetectionTaskEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/detection-tasks");

        api.MapPost("/{taskId}/image", async (
            string taskId,
            IFormFile image,
            IDetectionStorage storage,
            RuntimeConfigurationService configuration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return Results.BadRequest(ApiResponse<ImageUploadResponse>.Fail("taskId 不能为空。"));
            }

            if (image is null)
            {
                return Results.BadRequest(ApiResponse<ImageUploadResponse>.Fail("缺少图片文件字段 image。"));
            }

            var result = await storage.SaveSourceImageAsync(
                taskId,
                image,
                configuration.Current.PublicBaseUrl,
                cancellationToken);

            return Results.Ok(ApiResponse<ImageUploadResponse>.Ok(result, "图片上传完成。"));
        })
        .DisableAntiforgery();

        api.MapGet("/{taskId}/result", (
            string taskId,
            ServerPathInitializer paths,
            RuntimeConfigurationService configuration) =>
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return Results.BadRequest(ApiResponse<object>.Fail("taskId 不能为空。"));
            }

            var archiveRoot = paths.ArchiveRoot;
            if (!Directory.Exists(archiveRoot))
            {
                return Results.NotFound(ApiResponse<object>.Fail("尚未生成检测归档。"));
            }

            var resultPath = Directory
                .EnumerateFiles(archiveRoot, "detection-result.json", SearchOption.AllDirectories)
                .Where(path => path.Contains(taskId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (resultPath is null)
            {
                return Results.NotFound(ApiResponse<object>.Fail("检测结果尚未生成。"));
            }

            var resultNode = JsonNode.Parse(File.ReadAllText(resultPath));
            RewritePublicFileUrls(resultNode, configuration.Current.PublicBaseUrl);

            return Results.Ok(ApiResponse<JsonNode>.Ok(resultNode!, "检测结果读取完成。"));
        });

        return app;
    }

    private static void RewritePublicFileUrls(JsonNode? node, string publicBaseUrl)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var propertyName in jsonObject.Select(property => property.Key).ToArray())
                {
                    var child = jsonObject[propertyName];
                    if (TryRewriteFileUrl(child, publicBaseUrl, out var rewrittenUrl))
                    {
                        jsonObject[propertyName] = rewrittenUrl;
                    }
                    else
                    {
                        RewritePublicFileUrls(child, publicBaseUrl);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var child = jsonArray[index];
                    if (TryRewriteFileUrl(child, publicBaseUrl, out var rewrittenUrl))
                    {
                        jsonArray[index] = rewrittenUrl;
                    }
                    else
                    {
                        RewritePublicFileUrls(child, publicBaseUrl);
                    }
                }

                break;
        }
    }

    private static bool TryRewriteFileUrl(JsonNode? node, string publicBaseUrl, out string rewrittenUrl)
    {
        rewrittenUrl = string.Empty;

        if (node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        const string filesMarker = "/files/";
        var pathAndQuery = uri.PathAndQuery;
        var markerIndex = pathAndQuery.IndexOf(filesMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        rewrittenUrl = $"{publicBaseUrl.TrimEnd('/')}{pathAndQuery[markerIndex..]}";
        return true;
    }
}
