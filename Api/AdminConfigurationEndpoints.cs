using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminConfigurationEndpoints
{
    public static WebApplication MapAdminConfigurationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/config")
            .RequireAuthorization();

        api.MapGet("/", (RuntimeConfigurationService configuration) =>
            Results.Ok(ApiResponse<RuntimeConfigurationSnapshot>.Ok(
                configuration.GetSnapshot(),
                "配置读取完成。")));

        api.MapPost("/validate", (
            AdminConfigurationForm form,
            RuntimeConfigurationService configuration) =>
        {
            var result = configuration.Validate(form.ToOptions());
            return Results.Ok(ApiResponse<RuntimeConfigurationValidationResult>.Ok(
                result,
                result.IsValid ? "配置校验通过。" : "配置校验未通过。"));
        });

        api.MapPut("/", async (
            AdminConfigurationForm form,
            RuntimeConfigurationService configuration,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await configuration.SaveAsync(form, cancellationToken);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.RuntimeConfiguration,
                "保存系统配置",
                result.Success ? AdminAuditOutcomes.Success : AdminAuditOutcomes.Failure,
                result.Message,
                "系统配置",
                new Dictionary<string, string?>
                {
                    ["变更字段数量"] = result.ChangedPaths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["变更字段"] = string.Join("，", result.ChangedPaths)
                },
                cancellationToken);
            return result.Success
                ? Results.Ok(ApiResponse<RuntimeConfigurationSaveResult>.Ok(result, result.Message))
                : Results.BadRequest(ApiResponse<RuntimeConfigurationSaveResult>.Fail(result.Message));
        });

        return app;
    }
}
