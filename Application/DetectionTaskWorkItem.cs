using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public sealed record DetectionTaskWorkItem(
    DetectionTaskRecord Record,
    string RawMessage);
