using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Infrastructure.Mqtt;

public sealed class MqttDetectionResultPublisher : IDetectionResultPublisher, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<MqttDetectionResultPublisher> logger;
    private readonly RuntimeConfigurationService configuration;
    private readonly IMqttClient mqttClient;
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly OpsStateService ops;
    private MqttConfigurationSnapshot? connectedConfiguration;

    public MqttDetectionResultPublisher(
        ILogger<MqttDetectionResultPublisher> logger,
        RuntimeConfigurationService configuration,
        OpsStateService ops)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.ops = ops;

        var factory = new MqttClientFactory();
        mqttClient = factory.CreateMqttClient();
    }

    public Task PublishProgressAsync(
        DetectionTaskRecord record,
        string stage,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new DetectionProgressMessage
        {
            TaskId = record.TaskId,
            DeviceId = record.DeviceId,
            SiteId = record.SiteId,
            Stage = stage,
            Status = record.Status.ToString(),
            Message = message,
            CreatedAt = DateTimeOffset.Now
        };

        var mqtt = MqttConfigurationSnapshot.From(configuration.Current.Mqtt);
        var topic = FormatTopic(mqtt.TaskProgressTopicTemplate, record);
        return PublishJsonAsync(mqtt, topic, payload, cancellationToken);
    }

    public Task PublishResultAsync(
        DetectionTaskRecord record,
        DetectionPipelineResult result,
        CancellationToken cancellationToken)
    {
        var payload = new DetectionResultMessage
        {
            TaskId = record.TaskId,
            DeviceId = record.DeviceId,
            SiteId = record.SiteId,
            Success = result.Success,
            Status = result.Status,
            Message = result.Message,
            Summary = result.Summary,
            ResultJsonUrl = result.ResultJsonUrl,
            ReportUrl = result.ReportUrl,
            VisualSummaryUrl = result.VisualSummaryUrl,
            ErrorMessage = result.ErrorMessage,
            CreatedAt = DateTimeOffset.Now
        };

        var mqtt = MqttConfigurationSnapshot.From(configuration.Current.Mqtt);
        var topic = FormatTopic(mqtt.TaskResultTopicTemplate, record);
        return PublishJsonAsync(mqtt, topic, payload, cancellationToken);
    }

    private async Task PublishJsonAsync<T>(
        MqttConfigurationSnapshot mqtt,
        string topic,
        T payload,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(mqtt, cancellationToken);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        var result = await mqttClient.PublishAsync(message, cancellationToken);
        ops.RecordMqttState("publisher", "connected", $"最后发布 Topic={topic}, Result={result.ReasonCode}");
        logger.LogInformation(
            "MQTT task update published. Topic={Topic}, Result={Result}, PacketId={PacketId}",
            topic,
            result.ReasonCode,
            result.PacketIdentifier);
    }

    private async Task EnsureConnectedAsync(
        MqttConfigurationSnapshot mqtt,
        CancellationToken cancellationToken)
    {
        if (mqttClient.IsConnected && mqtt.Equals(connectedConfiguration))
        {
            return;
        }

        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (mqttClient.IsConnected && mqtt.Equals(connectedConfiguration))
            {
                return;
            }

            if (mqttClient.IsConnected)
            {
                ops.RecordMqttState("publisher", "configured", "MQTT 配置已更新，正在断开发布连接。");
                await mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
                connectedConfiguration = null;
            }

            var builder = new MqttClientOptionsBuilder()
                .WithClientId($"{mqtt.ClientId}-publisher")
                .WithTcpServer(mqtt.BrokerHost, mqtt.BrokerPort)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(10));

            if (!string.IsNullOrWhiteSpace(mqtt.UserName))
            {
                builder.WithCredentials(mqtt.UserName, mqtt.Password);
            }

            var result = await mqttClient.ConnectAsync(builder.Build(), cancellationToken);
            connectedConfiguration = mqtt;
            logger.LogInformation(
                "MQTT publisher connected. Broker={Broker}, Result={Result}",
                mqtt.BrokerEndpoint,
                result.ResultCode);
            ops.RecordMqttState("publisher", "connected", $"Broker={mqtt.BrokerEndpoint}, Result={result.ResultCode}");
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private static string FormatTopic(string template, DetectionTaskRecord record) =>
        template
            .Replace("{siteId}", record.SiteId, StringComparison.OrdinalIgnoreCase)
            .Replace("{deviceId}", record.DeviceId, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        connectionLock.Dispose();
    }
}
