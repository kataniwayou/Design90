using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Correlation;
using Shared.Extensions;
using Shared.MassTransit.Events;
using Shared.Models;
using Processor.Base.Interfaces;
using Processor.Base.Models;
using System.Diagnostics;

namespace Processor.Base.Services;

/// <summary>
/// Background service that processes activities from the queue and publishes events
/// </summary>
public class ActivityProcessingService : BackgroundService
{
    private readonly ActivityProcessingQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityProcessingService> _logger;
    private readonly int _workerCount;
    private static readonly ActivitySource ActivitySource = new(ActivitySources.Services);

    public ActivityProcessingService(
        ActivityProcessingQueue queue,
        IServiceProvider serviceProvider,
        ILogger<ActivityProcessingService> logger,
        IOptions<ProcessorConfiguration> config)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _workerCount = config.Value.BackgroundWorkerCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting {WorkerCount} background workers for activity processing", _workerCount);

        // Create multiple worker tasks
        var workerTasks = Enumerable.Range(0, _workerCount)
            .Select(workerId => StartWorker(workerId, stoppingToken))
            .ToArray();

        // Wait for all workers to complete
        await Task.WhenAll(workerTasks);

        _logger.LogInformation("All {WorkerCount} background workers stopped", _workerCount);
    }

    private async Task StartWorker(int workerId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background worker {WorkerId} started", workerId);

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessRequestAsync(request, workerId, stoppingToken);
                }
                finally
                {
                    // Decrement queue depth counter
                    _queue.DecrementQueueDepth();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
            _logger.LogInformation("Background worker {WorkerId} cancelled during shutdown", workerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background worker {WorkerId} failed with exception", workerId);
            throw; // Re-throw to fail the service
        }
        finally
        {
            _logger.LogInformation("Background worker {WorkerId} stopped", workerId);
        }
    }

    private async Task ProcessRequestAsync(ProcessingRequest request, int workerId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        using var activity = ActivitySource.StartActivityWithCorrelation("ProcessActivityFromQueue");
        
        var stopwatch = Stopwatch.StartNew();
        var command = request.OriginalCommand;
        var correlationId = request.CorrelationId;

        // Set correlation context for this processing thread
        var correlationIdContext = scope.ServiceProvider.GetRequiredService<ICorrelationIdContext>();
        correlationIdContext.Set(correlationId);

        // Set activity tags
        activity?.SetTag("correlation.id", correlationId.ToString())
                ?.SetBaggage("correlation.id", correlationId.ToString())
                ?.SetActivityExecutionTagsWithCorrelation(
                    command.OrchestratedFlowEntityId,
                    command.StepId,
                    command.ExecutionId)
                ?.SetEntityTags(command.Entities.Count);

        try
        {
            // Get required services
            var processorService = scope.ServiceProvider.GetRequiredService<IProcessorService>();
            var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            var config = scope.ServiceProvider.GetRequiredService<IOptions<ProcessorConfiguration>>().Value;
            var flowMetricsService = scope.ServiceProvider.GetService<IProcessorFlowMetricsService>();

            _logger.LogInformationWithCorrelation(
                "Processing activity from queue. WorkerId: {WorkerId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, QueueTime: {QueueTime}ms",
                workerId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId,
                (DateTime.UtcNow - request.ReceivedAt).TotalMilliseconds);

            // Process the activity
            var responses = await processorService.ProcessActivityAsync(request.ActivityMessage);

            stopwatch.Stop();

            // Publish events for each response
            foreach (var response in responses)
            {
                activity?.SetTag($"ActivityStatus_{response.ExecutionId}", response.Status.ToString());

                _logger.LogInformationWithCorrelation(
                    "Activity item completed from queue. WorkerId: {WorkerId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, OriginalExecutionId: {OriginalExecutionId}, ResultExecutionId: {ResultExecutionId}, Status: {Status}, Duration: {Duration}ms",
                    workerId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, response.ExecutionId, response.Status, stopwatch.ElapsedMilliseconds);

                if (response.Status == ActivityExecutionStatus.Completed)
                {
                    await publishEndpoint.Publish(new ActivityExecutedEvent
                    {
                        ProcessorId = command.ProcessorId,
                        OrchestratedFlowEntityId = command.OrchestratedFlowEntityId,
                        StepId = command.StepId,
                        ExecutionId = response.ExecutionId,
                        CorrelationId = correlationId,
                        PublishId = command.PublishId,
                        Duration = response.Duration,
                        Status = response.Status,
                        EntitiesProcessed = command.Entities.Count,
                    }, cancellationToken);

                    flowMetricsService?.RecordEventPublished(true, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);
                }
                else
                {
                    await publishEndpoint.Publish(new ActivityFailedEvent
                    {
                        ProcessorId = command.ProcessorId,
                        OrchestratedFlowEntityId = command.OrchestratedFlowEntityId,
                        StepId = command.StepId,
                        ExecutionId = response.ExecutionId,
                        CorrelationId = correlationId,
                        PublishId = command.PublishId,
                        Duration = response.Duration,
                        ErrorMessage = response.ErrorMessage ?? "Unknown error",
                        EntitiesBeingProcessed = command.Entities.Count,
                    }, cancellationToken);

                    flowMetricsService?.RecordEventPublished(true, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);
                }
            }

            activity?.SetTag(ActivityTags.ActivityDuration, stopwatch.ElapsedMilliseconds)
                    ?.SetTag("ResponseCount", responses.Count());

            _logger.LogInformationWithCorrelation(
                "Activity collection completed from queue. WorkerId: {WorkerId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, OriginalExecutionId: {OriginalExecutionId}, ResponseCount: {ResponseCount}, Duration: {Duration}ms",
                workerId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, responses.Count(), stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await HandleProcessingErrorAsync(request, ex, scope, workerId, cancellationToken);
        }
    }

    private async Task HandleProcessingErrorAsync(ProcessingRequest request, Exception ex, IServiceScope scope, int workerId, CancellationToken cancellationToken)
    {
        var command = request.OriginalCommand;
        var correlationId = request.CorrelationId;

        // Get required services for error handling
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var config = scope.ServiceProvider.GetRequiredService<IOptions<ProcessorConfiguration>>().Value;
        var flowMetricsService = scope.ServiceProvider.GetService<IProcessorFlowMetricsService>();
        var healthMetricsService = scope.ServiceProvider.GetService<IProcessorHealthMetricsService>();

        // Record metrics
        flowMetricsService?.RecordCommandConsumed(false, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);
        healthMetricsService?.RecordException(ex.GetType().Name, "critical", correlationId);

        _logger.LogErrorWithCorrelation(ex,
            "Failed to process activity from queue. WorkerId: {WorkerId}, ProcessorId: {ProcessorId}, OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, RetryCount: {RetryCount}",
            workerId, command.ProcessorId, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, request.RetryCount);

        // Publish failure event
        await publishEndpoint.Publish(new ActivityFailedEvent
        {
            ProcessorId = command.ProcessorId,
            OrchestratedFlowEntityId = command.OrchestratedFlowEntityId,
            StepId = command.StepId,
            ExecutionId = command.ExecutionId,
            CorrelationId = correlationId,
            PublishId = command.PublishId,
            Duration = TimeSpan.Zero,
            ErrorMessage = ex.Message,
            ExceptionType = ex.GetType().Name,
            StackTrace = ex.StackTrace,
            EntitiesBeingProcessed = command.Entities.Count,
        }, cancellationToken);

        flowMetricsService?.RecordEventPublished(true, command.OrchestratedFlowEntityId, command.StepId, command.ExecutionId, correlationId);
    }
}
