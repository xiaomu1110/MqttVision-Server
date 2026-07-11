using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;

namespace MqttVision.Server.Api;

public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/sign-in", async (
            HttpRequest request,
            AdminAuthenticationService auth,
            AdminAuditService audit,
            HttpContext context) =>
        {
            var form = await request.ReadFormAsync();
            var password = form["password"].FirstOrDefault();
            if (!auth.IsEnabled)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Authentication,
                    "网页登录",
                    AdminAuditOutcomes.Failure,
                    "管理员密码未配置，网页登录被拒绝。");
                return Results.Redirect("/admin/login?disabled=1");
            }

            if (!auth.ValidatePassword(password))
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Authentication,
                    "网页登录",
                    AdminAuditOutcomes.Failure,
                    "管理员密码错误，网页登录被拒绝。");
                return Results.Redirect("/admin/login?error=1");
            }

            await auth.SignInAsync(context);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Authentication,
                "网页登录",
                AdminAuditOutcomes.Success,
                "管理员网页登录成功。");
            return Results.Redirect("/admin/settings");
        })
        .DisableAntiforgery();

        app.MapPost("/admin/logout", async (
            AdminAuthenticationService auth,
            AdminAuditService audit,
            HttpContext context) =>
        {
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Authentication,
                "网页退出",
                AdminAuditOutcomes.Success,
                "管理员已退出网页登录。");
            await auth.SignOutAsync(context);
            return Results.Redirect("/admin/login");
        })
        .RequireAuthorization()
        .DisableAntiforgery();

        var api = app.MapGroup("/api/admin/auth");

        api.MapGet("/status", (
            AdminAuthenticationService auth,
            HttpContext context) => Results.Ok(ApiResponse<object>.Ok(new
            {
                enabled = auth.IsEnabled,
                signedIn = context.User.Identity?.IsAuthenticated == true,
                name = context.User.Identity?.Name
            }, "管理员登录状态读取完成。")));

        api.MapPost("/login", async (
            AdminLoginRequest request,
            AdminAuthenticationService auth,
            AdminAuditService audit,
            HttpContext context) =>
        {
            if (!auth.IsEnabled)
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Authentication,
                    "接口登录",
                    AdminAuditOutcomes.Failure,
                    "管理员密码未配置，接口登录被拒绝。");
                return Results.BadRequest(ApiResponse<object>.Fail("管理员密码未配置，写入权限未启用。"));
            }

            if (!auth.ValidatePassword(request.Password))
            {
                await audit.RecordHttpAsync(
                    context,
                    AdminAuditCategories.Authentication,
                    "接口登录",
                    AdminAuditOutcomes.Failure,
                    "管理员密码错误，接口登录被拒绝。");
                return Results.Unauthorized();
            }

            await auth.SignInAsync(context);
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Authentication,
                "接口登录",
                AdminAuditOutcomes.Success,
                "管理员接口登录成功。");
            return Results.Ok(ApiResponse<object>.Ok(new { signedIn = true }, "登录完成。"));
        })
        .DisableAntiforgery();

        api.MapPost("/logout", async (
            AdminAuthenticationService auth,
            AdminAuditService audit,
            HttpContext context) =>
        {
            await audit.RecordHttpAsync(
                context,
                AdminAuditCategories.Authentication,
                "接口退出",
                AdminAuditOutcomes.Success,
                "管理员已退出接口登录。");
            await auth.SignOutAsync(context);
            return Results.Ok(ApiResponse<object>.Ok(new { signedIn = false }, "已退出登录。"));
        });

        return app;
    }
}

public sealed record AdminLoginRequest(string? Password);
