using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using MqttVision.Server.Api;
using MqttVision.Server.Application;
using MqttVision.Server.Components;
using MqttVision.Server.Configuration;
using MqttVision.Server.Infrastructure.Mqtt;
using MqttVision.Server.Infrastructure.Cad;
using MqttVision.Server.Infrastructure.Storage;
using MqttVision.Server.Infrastructure.Vision;
using MqttVision.Server.Operations;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMqttVisionYaml(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

var configuredStorageRoot = builder.Configuration["MqttVision:StorageRoot"];
if (string.IsNullOrWhiteSpace(configuredStorageRoot))
{
    configuredStorageRoot = "runtime";
}

var resolvedStorageRoot = Path.IsPathRooted(configuredStorageRoot)
    ? Path.GetFullPath(configuredStorageRoot)
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredStorageRoot));
var dataProtectionKeyPath = Path.Combine(resolvedStorageRoot, "dataprotection-keys");
Directory.CreateDirectory(dataProtectionKeyPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
    .SetApplicationName("MqttVision.Server");

builder.Services.Configure<MqttVisionServerOptions>(
    builder.Configuration.GetSection(MqttVisionServerOptions.SectionName));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/login";
        options.Cookie.Name = "MqttVision.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<AdminAuthenticationService>();
builder.Services.AddSingleton<RuntimeConfigurationService>();
builder.Services.AddSingleton<CabinetConfigurationAdminService>();
builder.Services.AddSingleton<ServerPathInitializer>();
builder.Services.AddSingleton<YoloModelFileService>();
builder.Services.AddSingleton<AdminConfigurationBackupService>();
builder.Services.AddSingleton<AdminAuditService>();
builder.Services.AddSingleton<AdminMaintenanceService>();
builder.Services.AddSingleton<ICadConfigurationParser, AsposeCadConfigurationParser>();
builder.Services.AddSingleton<CadImportService>();
builder.Services.AddSingleton<IDetectionStorage, FileSystemDetectionStorage>();
builder.Services.AddSingleton<IDetectionTaskQueue, ChannelDetectionTaskQueue>();
builder.Services.AddSingleton<IObjectDetector, YoloOnnxObjectDetector>();
builder.Services.AddHttpClient<ITextRecognizer, PaddleOcrServingTextRecognizer>();
builder.Services.AddSingleton<IDetectionPipeline, DetectionPipeline>();
builder.Services.AddSingleton<IDetectionResultPublisher, MqttDetectionResultPublisher>();
builder.Services.AddSingleton<DetectionTaskWorkflow>();
builder.Services.AddSingleton<OpsStateService>();
builder.Services.AddHostedService<MqttTaskSubscriberService>();
builder.Services.AddHostedService<DetectionTaskProcessorService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<CadImportService>());
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

var pathInitializer = app.Services.GetRequiredService<ServerPathInitializer>();
pathInitializer.EnsureDirectories();

app.UseCors();
app.UseStaticFiles();
app.MapStaticAssets();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pathInitializer.StorageRoot),
    RequestPath = "/files"
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "MqttVision.Server",
    time = DateTimeOffset.Now
}));

app.MapDetectionTaskEndpoints();
app.MapOpsEndpoints();
app.MapAdminAuthEndpoints();
app.MapAdminConfigurationEndpoints();
app.MapAdminYoloModelEndpoints();
app.MapAdminCabinetConfigurationEndpoints();
app.MapAdminCadImportEndpoints();
app.MapAdminBackupEndpoints();
app.MapAdminAuditEndpoints();
app.MapAdminMaintenanceEndpoints();
app.MapGet("/", () => Results.Redirect("/ops"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
