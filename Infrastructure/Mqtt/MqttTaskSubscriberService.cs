using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MqttVision.Server.Application;
using MqttVision.Server.Configuration;
using MqttVision.Server.Contracts;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Infrastructure.Mqtt;

public sealed class MqttTaskSubscriberService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<MqttTaskSubscriberService> logger;
    private readonly DetectionTaskWorkflow workflow;
    private readonly RuntimeConfigurationService configuration;
    private readonly IMqttClient mqttClient;
    private readonly OpsStateService ops;
    private MqttConfigurationSnapshot? connectedConfiguration;

    public MqttTaskSubscriberService(
        ILogger<MqttTaskSubscriberService> logger,
        DetectionTaskWorkflow workflow,
        RuntimeConfigurationService configuration,
        OpsStateService ops)
    {
        this.logger = logger;
        this.workflow = workflow;
        this.configuration = configuration;
        this.ops = ops;

        var factory = new MqttClientFactory();
        mqttClient = factory.CreateMqttClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqttClient.ApplicationMessageReceivedAsync += async args =>
        {
            var payload = args.ApplicationMessage.ConvertPayloadToString();

            try
            {
                var message = JsonSerializer.Deserialize<DetectionTaskMessage>(payload, JsonOptions);
                if (message is null || string.IsNullOrWhiteSpace(message.TaskId))
                {
                    logger.LogWarning("Ignored invalid MQTT task message. Topic={Topic}", args.ApplicationMessage.Topic);
                    return;
                }

                await workflow.AcceptSubmittedTaskAsync(message, payload, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process MQTT task message. Topic={Topic}", args.ApplicationMessage.Topic);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (mqttClient.IsConnected && ShouldReconnect())
                {
                    await DisconnectForConfigurationChangeAsync(stoppingToken);
                }

                if (!mqttClient.IsConnected)
                {
                    await ConnectAndSubscribeAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MQTT subscriber loop failed. Retry in 5 seconds.");
                ops.RecordMqttState("subscriber", "error", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        var mqtt = MqttConfigurationSnapshot.From(configuration.Current.Mqtt);
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(mqtt.ClientId)
            .WithTcpServer(mqtt.BrokerHost, mqtt.BrokerPort)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10));

        if (!string.IsNullOrWhiteSpace(mqtt.UserName))
        {
            builder.WithCredentials(mqtt.UserName, mqtt.Password);
        }

        ops.RecordMqttState("subscriber", "configured", $"正在连接 {mqtt.BrokerEndpoint}。");
        var result = await mqttClient.ConnectAsync(builder.Build(), cancellationToken);
        logger.LogInformation(
            "MQTT connected. Broker={Broker}, ClientId={ClientId}, Result={Result}",
            mqtt.BrokerEndpoint,
            mqtt.ClientId,
            result.ResultCode);
        ops.RecordMqttState("subscriber", "connected", $"Broker={mqtt.BrokerEndpoint}, ClientId={mqtt.ClientId}, Result={result.ResultCode}");

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter
                .WithTopic(mqtt.TaskSubmitTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        try
        {
            await mqttClient.SubscribeAsync(subscribeOptions, cancellationToken);
        }
        catch
        {
            connectedConfiguration = null;
            if (mqttClient.IsConnected)
            {
                await mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
            }

            throw;
        }

        connectedConfiguration = mqtt;
        logger.LogInformation("MQTT subscribed. Topic={Topic}", mqtt.TaskSubmitTopic);
        ops.RecordMqttState("subscriber", "connected", $"已订阅 {mqtt.TaskSubmitTopic}");
    }

    private bool ShouldReconnect()
    {
        var current = MqttConfigurationSnapshot.From(configuration.Current.Mqtt);
        return connectedConfiguration is null || !current.Equals(connectedConfiguration);
    }

    private async Task DisconnectForConfigurationChangeAsync(CancellationToken cancellationToken)
    {
        ops.RecordMqttState("subscriber", "configured", "MQTT 配置已更新，正在断开旧连接。");
        await mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        connectedConfiguration = null;
    }
}
