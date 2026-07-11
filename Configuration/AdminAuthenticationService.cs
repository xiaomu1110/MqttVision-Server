using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MqttVision.Server.Configuration;

public sealed class AdminAuthenticationService
{
    private const string PasswordEnvironmentVariable = "MQTTVISION_ADMIN_PASSWORD";
    private readonly IConfiguration configuration;

    public AdminAuthenticationService(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ReadPassword());

    public bool ValidatePassword(string? password)
    {
        var expected = ReadPassword();
        return !string.IsNullOrWhiteSpace(expected) &&
            string.Equals(password, expected, StringComparison.Ordinal);
    }

    public async Task SignInAsync(HttpContext context)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "管理员"),
            new Claim(ClaimTypes.Role, "Administrator")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    public Task SignOutAsync(HttpContext context) =>
        context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    private string? ReadPassword() =>
        Environment.GetEnvironmentVariable(PasswordEnvironmentVariable) ??
        configuration["Admin:Password"];
}
