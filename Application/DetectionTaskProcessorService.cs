using MqttVision.Server.Domain;
using MqttVision.Server.Infrastructure.Mqtt;
using MqttVision.Server.Infrastructure.Storage;
using MqttVision.Server.Operations;

namespace MqttVision.Server.Application;

public sealed class DetectionTaskProcessorService : BackgroundService
{
    private readonly ILogger<DetectionTaskProcessorService> logger;
    private readonly IDetectionTaskQueue queue;
    private readonly IDetectionPipeline pipeline;
    private readonly IDetectionStorage storage;
    private readonly IDetectionResultPublisher publisher;
    private readonly OpsStateService ops;

    public DetectionTaskProcessorService(
        ILogger<DetectionTaskProcessorService> logger,
        IDetectionTaskQueue queue,
        IDetectionPipeline pipeline,
        IDetectionStorage storage,
        IDetectionResultPublisher publisher,
        OpsStateService ops)
    {
        this.logger = logger;
        this.queue = queue;
        this.pipeline = pipeline;
        this.storage = storage;
        this.publisher = publisher;
        this.ops = ops;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.DequeueAllAsync(stoppingToken))
        {
            await ProcessOneAsync(item, stoppingToken);
        }
    }

    private async Task ProcessOneAsync(DetectionTaskWorkItem item, CancellationToken cancellationToken)
    {
        var record = item.Record;

        try
        {
            await MoveToAsync(record, DetectionTaskStatus.Processing, "processing", "上位机开始处理检测任务。", cancellationToken);

            var result = await pipeline.ProcessAsync(record, cancellationToken);

            record.Status = DetectionTaskStatus.Completed;
            record.ResultJsonUrl = result.ResultJsonUrl;
            record.ReportUrl = result.ReportUrl;
            record.VisualSummaryUrl = result.VisualSummaryUrl;
            record.ErrorMessage = result.ErrorMessage;
            record.UpdatedAt = DateTimeOffset.Now;
            await storage.SaveTaskRecordAsync(record, cancellationToken);
            ops.RecordTaskResult(record, result);

            await PublishProgressSafelyAsync(record, "completed", "检测任务处理完成。", cancellationToken);
            await PublishResultSafelyAsync(record, result, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Detection task processing failed. TaskId={TaskId}", record.TaskId);

            record.Status = DetectionTaskStatus.Failed;
            record.ErrorMessage = ex.Message;
            record.UpdatedAt = DateTimeOffset.Now;
            await storage.SaveTaskRecordAsync(record, cancellationToken);
            ops.RecordTaskFailure(record, ex.Message);

            var result = DetectionPipelineResult.Failed(ex.Message);
            await PublishProgressSafelyAsync(record, "failed", $"检测任务处理失败: {ex.Message}", cancellationToken);
            await PublishResultSafelyAsync(record, result, cancellationToken);
        }
    }

    private async Task MoveToAsync(
        DetectionTaskRecord record,
        DetectionTaskStatus status,
        string stage,
        string message,
        CancellationToken cancellationToken)
    {
        record.Status = status;
        record.UpdatedAt = DateTimeOffset.Now;
        await storage.SaveTaskRecordAsync(record, cancellationToken);
        ops.RecordTaskStage(record, stage, message);
        await PublishProgressSafelyAsync(record, stage, message, cancellationToken);
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
            logger.LogWarning(ex, "Failed to publish task progress. TaskId={TaskId}, Stage={Stage}", record.TaskId, stage);
        }
    }

    private async Task PublishResultSafelyAsync(
        DetectionTaskRecord record,
        DetectionPipelineResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishResultAsync(record, result, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish task result. TaskId={TaskId}", record.TaskId);
        }
    }
}
