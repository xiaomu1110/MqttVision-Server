using MqttVision.Server.Contracts;
using MqttVision.Server.Domain;
using MqttVision.Server.Infrastructure.Mqtt;
using MqttVision.Server.Infrastructure.Storage;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Application;

public sealed class DetectionTaskWorkflow
{
    private readonly ILogger<DetectionTaskWorkflow> logger;
    private readonly IDetectionStorage storage;
    private readonly IDetectionTaskQueue queue;
    private readonly IDetectionResultPublisher publisher;
    private readonly OpsStateService ops;

    public DetectionTaskWorkflow(
        ILogger<DetectionTaskWorkflow> logger,
        IDetectionStorage storage,
        IDetectionTaskQueue queue,
        IDetectionResultPublisher publisher,
        OpsStateService ops)
    {
        this.logger = logger;
        this.storage = storage;
        this.queue = queue;
        this.publisher = publisher;
        this.ops = ops;
    }

    public async Task AcceptSubmittedTaskAsync(
        DetectionTaskMessage message,
        string rawMessage,
        CancellationToken cancellationToken)
    {
        var record = new DetectionTaskRecord
        {
            TaskId = message.TaskId,
            DeviceId = message.DeviceId,
            OperatorId = message.OperatorId,
            SiteId = message.SiteId,
            CabinetId = message.CabinetId,
            Image = message.Image,
            Status = DetectionTaskStatus.MqttSubmitted,
            CreatedAt = message.CreatedAt == default ? DateTimeOffset.Now : message.CreatedAt,
            UpdatedAt = DateTimeOffset.Now,
            RawMessage = rawMessage
        };

        await storage.SaveTaskRecordAsync(record, cancellationToken);
        ops.RecordTaskStage(record, "mqtt-received", "服务端已收到 MQTT 检测任务。");
        await PublishProgressSafelyAsync(record, "mqtt-received", "服务端已收到 MQTT 检测任务。", cancellationToken);

        record.Status = DetectionTaskStatus.Queued;
        record.UpdatedAt = DateTimeOffset.Now;
        await storage.SaveTaskRecordAsync(record, cancellationToken);
        await queue.QueueAsync(new DetectionTaskWorkItem(record, rawMessage), cancellationToken);
        ops.RecordTaskStage(record, "queued", "检测任务已进入上位机处理队列。");
        await PublishProgressSafelyAsync(record, "queued", "检测任务已进入上位机处理队列。", cancellationToken);

        logger.LogInformation(
            "Detection task accepted and queued. TaskId={TaskId}, DeviceId={DeviceId}, TransferMode={TransferMode}, Url={Url}",
            message.TaskId,
            message.DeviceId,
            message.Image.TransferMode,
            message.Image.Url);
    }

    private async Task PublishProgressSafelyAsync(
        DetectionTaskRecord record,
        string stage,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishProgressAsync(record, stage, message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish progress. TaskId={TaskId}, Stage={Stage}", record.TaskId, stage);
        }
    }
}
