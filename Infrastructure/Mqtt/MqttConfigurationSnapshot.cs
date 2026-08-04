using MqttVision.Server.Configuration;

namespace MqttVision.Server.Infrastructure.Mqtt;

internal sealed record MqttConfigurationSnapshot(
    string BrokerHost,
    int BrokerPort,
    string? UserName,
    string? Password,
    string ClientId,
    string TaskSubmitTopic,
    string TaskProgressTopicTemplate,
    string TaskResultTopicTemplate)
{
    public static MqttConfigurationSnapshot From(MqttOptions options) =>
        new(
            options.BrokerHost,
            options.BrokerPort,
            options.UserName,
            options.Password,
            options.ClientId,
            options.TaskSubmitTopic,
            options.TaskProgressTopicTemplate,
            options.TaskResultTopicTemplate);

    public string BrokerEndpoint => $"{BrokerHost}:{BrokerPort}";
}
