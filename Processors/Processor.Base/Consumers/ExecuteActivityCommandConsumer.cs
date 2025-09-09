using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Correlation;
using Shared.Extensions;
using Shared.MassTransit.Commands;
using Shared.MassTransit.Events;
using Shared.Models;
using Processor.Base.Interfaces;
using Processor.Base.Models;
using System.Diagnostics;


namespace Processor.Base.Consumers;

/// <summary>
/// Consumer for ExecuteActivityCommand messages
/// </summary>
public class ExecuteActivityCommandConsumer : IConsumer<ExecuteActivityCommand>
{
    private readonly IProcessorService _processorService;
    private readonly IActivityProcessingQueue _processingQueue;
    private readonly IProcessorFlowMetricsService? _flowMetricsService;
    private readonly IProcessorHealthMetricsService? _healthMetricsService;
    private readonly ProcessorConfiguration _config;
    private readonly ILogger<ExecuteActivityCommandConsumer> _logger;
    private readonly ICorrelationIdContext _correlationIdContext;
    private static readonly ActivitySource ActivitySource = new(ActivitySources.Services);

    public ExecuteActivityCommandConsumer(
        IProcessorService processorService,
        IActivityProcessingQueue processingQueue,
        ILogger<ExecuteActivityCommandConsumer> logger,
        ICorrelationIdContext correlationIdContext,
        IOptions<ProcessorConfiguration> config,
        IProcessorFlowMetricsService? flowMetricsService = null,
        IProcessorHealthMetricsService? healthMetricsService = null)
    {
        _processorService = processorService;
        _processingQueue = processingQueue;
        _logger = logger;
        _correlationIdContext = correlationIdContext;
        _config = config.Value;
        _flowMetricsService = flowMetricsService;
        _healthMetricsService = healthMetricsService;

        // Add debug logging to verify consumer is being created
        _logger.LogInformationWithCorrelation("ExecuteActivityCommandConsumer created and registered successfully with queue handoff");
        Console.WriteLine("✅ ExecuteActivityCommandConsumer instantiated with queue handoff pattern");
    }

    public async Task Consume(ConsumeContext<ExecuteActivityCommand> context)
    {
        // Extract correlation ID from MassTransit context or message
        var correlationId = ExtractCorrelationId(context);

        // Set the correlation ID in the context for proper logging
        _correlationIdContext.Set(correlationId);

        // Also set in current activity
        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            currentActivity.SetTag("correlation.id", correlationId.ToString());
            currentActivity.SetBaggage("correlation.id", correlationId.ToString());
        }

        using var activity = ActivitySource.StartActivityWithCorrelation("ExecuteActivityCommandConsumer");
        var command = context.Message;
        var stopwatch = Stopwatch.StartNew();

        activity?.SetMessageTagsWithCorrelation(nameof(ExecuteActivityCommand), nameof(ExecuteActivityCommandConsumer))
                ?.SetActivityExecutionTagsWithCorrelation(
                    command.OrchestratedFlowEntityId,
                    command.StepId,
                    command.ExecutionId)
                ?.SetEntityTags(command.Entities.Count);

        _logger.LogInformationWithCorrelation(
            "Workflow step received for queue handoff. OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, ProcessorId: {ProcessorId}, EntitiesCount: {EntitiesCount}",
            command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, command.ProcessorId, command.Entities.Count);

        try
        {
            // Get current processor ID once
            var currentProcessorId = await _processorService.GetProcessorIdAsync();

            _logger.LogDebugWithCorrelation(
                "Validating ExecuteActivityCommand. TargetProcessorId: {TargetProcessorId}, CurrentProcessorId: {CurrentProcessorId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                command.ProcessorId, currentProcessorId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId);

            // Special handling for uninitialized processor
            if (currentProcessorId == Guid.Empty)
            {
                _logger.LogWarningWithCorrelation(
                    "Processor not yet initialized (ProcessorId is empty). Rejecting message and requeueing. TargetProcessorId: {TargetProcessorId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                    command.ProcessorId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId);

                // Throw an exception to trigger MassTransit retry mechanism
                throw new InvalidOperationException($"Processor not yet initialized. ProcessorId is empty. Message will be retried.");
            }

            // Check if this message is for this processor instance (direct comparison)
            if (currentProcessorId != command.ProcessorId)
            {
                _logger.LogWarningWithCorrelation(
                    "Message not for this processor instance. TargetProcessorId: {TargetProcessorId}, CurrentProcessorId: {CurrentProcessorId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                    command.ProcessorId, currentProcessorId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId);
                return;
            }

            // Record command consumption metrics (successful consumption)
            _flowMetricsService?.RecordCommandConsumed(true, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);

            // Convert command to activity message
            var activityMessage = new ProcessorActivityMessage
            {
                ProcessorId = command.ProcessorId,
                OrchestratedFlowEntityId = command.OrchestratedFlowEntityId,
                StepId = command.StepId,
                ExecutionId = command.ExecutionId,
                Entities = command.Entities,
                CorrelationId = correlationId, // Use extracted correlation ID
                PublishId = command.PublishId,
                CreatedAt = command.CreatedAt
            };

            // Create processing request for queue handoff
            var processingRequest = new ProcessingRequest
            {
                OriginalCommand = command,
                ActivityMessage = activityMessage,
                CorrelationId = correlationId,
                ReceivedAt = DateTime.UtcNow,
                RetryCount = 0,
                MaxRetries = 3
            };

            // Quick handoff to processing queue
            await _processingQueue.EnqueueAsync(processingRequest, context.CancellationToken);

            stopwatch.Stop();

            // Set telemetry for quick handoff
            activity?.SetTag(ActivityTags.ActivityDuration, stopwatch.ElapsedMilliseconds)
                    ?.SetTag("HandoffType", "QueueHandoff")
                    ?.SetTag("QueueDepth", _processingQueue.GetQueueDepth());

            _logger.LogInformationWithCorrelation(
                "Workflow step handed off to processing queue. OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, ProcessorId: {ProcessorId}, HandoffDuration: {Duration}ms, QueueDepth: {QueueDepth}",
                command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, command.ProcessorId, stopwatch.ElapsedMilliseconds, _processingQueue.GetQueueDepth());

            // Consumer thread is now immediately available for next message
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Record failed command consumption
            _flowMetricsService?.RecordCommandConsumed(false, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);

            // Record exception metrics
            _healthMetricsService?.RecordException(ex.GetType().Name, "critical", correlationId);

            activity?.SetErrorTags(ex)
                ?.SetTag(ActivityTags.ActivityStatus, ActivityExecutionStatus.Failed.ToString())
                ?.SetTag(ActivityTags.ActivityDuration, stopwatch.ElapsedMilliseconds);

            _logger.LogErrorWithCorrelation(ex,
                "Failed to handoff activity to processing queue. ProcessorId: {ProcessorId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, Duration: {Duration}ms",
                command.ProcessorId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, stopwatch.ElapsedMilliseconds);


            // Publish failure event for exception cases (queue handoff failures)
            await context.Publish(new ActivityFailedEvent
            {
                ProcessorId = command.ProcessorId,
                OrchestratedFlowEntityId = command.OrchestratedFlowEntityId,
                StepId = command.StepId,
                ExecutionId = command.ExecutionId,
                CorrelationId = correlationId, // Use extracted correlation ID
                PublishId = command.PublishId,
                Duration = stopwatch.Elapsed,
                ErrorMessage = $"Queue handoff failed: {ex.Message}",
                ExceptionType = ex.GetType().Name,
                StackTrace = ex.StackTrace,
                EntitiesBeingProcessed = command.Entities.Count,
            });

            // Record successful event publishing (even for failure events)
            _flowMetricsService?.RecordEventPublished(true, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);

            // Re-throw to trigger MassTransit error handling
            throw;
        }
    }

    /// <summary>
    /// Extract correlation ID from MassTransit context or message
    /// </summary>
    private static Guid ExtractCorrelationId(ConsumeContext<ExecuteActivityCommand> context)
    {
        // 1. Try to get from MassTransit's built-in correlation ID
        if (context.CorrelationId.HasValue && context.CorrelationId.Value != Guid.Empty)
        {
            return context.CorrelationId.Value;
        }

        // 2. Try to get from message headers
        if (context.Headers.TryGetHeader("X-Correlation-ID", out var headerValue) &&
            headerValue is string correlationIdString &&
            Guid.TryParse(correlationIdString, out var headerCorrelationId) &&
            headerCorrelationId != Guid.Empty)
        {
            return headerCorrelationId;
        }

        // 3. Try to get from message property (if not empty)
        if (context.Message.CorrelationId != Guid.Empty)
        {
            return context.Message.CorrelationId;
        }

        // 4. Try to get from current activity baggage
        var activity = Activity.Current;
        if (activity?.GetBaggageItem("correlation.id") is string baggageValue &&
            Guid.TryParse(baggageValue, out var baggageCorrelationId) &&
            baggageCorrelationId != Guid.Empty)
        {
            return baggageCorrelationId;
        }

        // 5. Generate a new correlation ID as fallback
        var newCorrelationId = Guid.NewGuid();

        // Set it in the current activity for future use
        if (activity != null)
        {
            activity.SetTag("correlation.id", newCorrelationId.ToString());
            activity.SetBaggage("correlation.id", newCorrelationId.ToString());
        }

        return newCorrelationId;
    }
}
