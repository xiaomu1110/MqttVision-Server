using Microsoft.Extensions.FileProviders;
using MqttVision.Server.Api;
using MqttVision.Server.Application;
using MqttVision.Server.Components;
using MqttVision.Server.Configuration;
using MqttVision.Server.Infrastructure.Mqtt;
using MqttVision.Server.Infrastructure.Storage;
using MqttVision.Server.Infrastructure.Vision;
using MqttVision.Server.Operations;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMqttVisionYaml(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.Configure<MqttVisionServerOptions>(
    builder.Configuration.GetSection(MqttVisionServerOptions.SectionName));

builder.Services.AddSingleton<ServerPathInitializer>();
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
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pathInitializer.StorageRoot),
    RequestPath = "/files"
});
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "MqttVision.Server",
    time = DateTimeOffset.Now
}));

app.MapDetectionTaskEndpoints();
app.MapOpsEndpoints();
app.MapGet("/", () => Results.Redirect("/ops"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
