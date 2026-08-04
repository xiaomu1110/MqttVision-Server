using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminCabinetConfigurationEndpoints
{
    public static WebApplication MapAdminCabinetConfigurationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/cabinets")
            .RequireAuthorization();

        api.MapGet("/", async (
            CabinetConfigurationAdminService cabinets,
            CancellationToken cancellationToken) =>
        {
            var summaries = await cabinets.ListAsync(cancellationToken);
            return Results.Ok(ApiResponse<IReadOnlyList<CabinetConfigurationSummary>>.Ok(
                summaries,
                "柜体配置列表读取完成。"));
        });

        api.MapGet("/{cabinetId}", async (
            string cabinetId,
            CabinetConfigurationAdminService cabinets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var cabinet = await cabinets.GetAsync(cabinetId, cancellationToken);
                return Results.Ok(ApiResponse<CabinetConfigurationEditorForm>.Ok(
                    cabinet,
                    "柜体配置读取完成。"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        });

        api.MapPost("/validate", (
            CabinetConfigurationEditorForm form,
            CabinetConfigurationAdminService cabinets) =>
        {
            var issues = cabinets.Validate(form.ToDomain());
            return Results.Ok(ApiResponse<IReadOnlyList<ConfigurationValidationIssue>>.Ok(
                issues,
                issues.Any(issue => !issue.IsWarning) ? "柜体配置校验未通过。" : "柜体配置校验通过。"));
        });

        api.MapPut("/{cabinetId}", async (
            string cabinetId,
            CabinetConfigurationEditorForm form,
            CabinetConfigurationAdminService cabinets,
            AdminAuditService audit,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            form.CabinetId = cabinetId.Trim();
            var result = await cabinets.SaveAsync(form, cancellationToken);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.CabinetConfiguration,
                "保存柜体配置",
                result.Success ? AdminAuditOutcomes.Success : AdminAuditOutcomes.Failure,
                result.Message,
                form.CabinetId,
                new Dictionary<string, string?>
                {
                    ["端子数量"] = form.Terminals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["提示数量"] = result.Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                cancellationToken);
            return result.Success
                ? Results.Ok(ApiResponse<CabinetConfigurationSaveResult>.Ok(result, result.Message))
                : Results.BadRequest(ApiResponse<CabinetConfigurationSaveResult>.Fail(result.Message));
        });

        return app;
    }
}
