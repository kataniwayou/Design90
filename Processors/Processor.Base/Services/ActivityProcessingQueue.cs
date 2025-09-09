using Microsoft.Extensions.Logging;
using Processor.Base.Interfaces;
using Processor.Base.Models;
using System.Threading.Channels;

namespace Processor.Base.Services;

/// <summary>
/// In-memory queue implementation for activity processing
/// </summary>
public class ActivityProcessingQueue : IActivityProcessingQueue
{
    private readonly ChannelWriter<ProcessingRequest> _writer;
    private readonly ChannelReader<ProcessingRequest> _reader;
    private readonly ILogger<ActivityProcessingQueue> _logger;
    private int _queueDepth = 0;

    public ActivityProcessingQueue(ILogger<ActivityProcessingQueue> logger)
    {
        _logger = logger;

        // Create bounded channel with backpressure
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait, // Backpressure to consumer
            SingleReader = false, // Multiple processing threads can read
            SingleWriter = false  // Multiple consumer threads can write
        };

        var channel = Channel.CreateBounded<ProcessingRequest>(options);
        _writer = channel.Writer;
        _reader = channel.Reader;

        _logger.LogInformation("ActivityProcessingQueue initialized with capacity: 1000");
    }

    /// <summary>
    /// Gets the channel reader for the background service
    /// </summary>
    public ChannelReader<ProcessingRequest> Reader => _reader;

    public async Task EnqueueAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await _writer.WriteAsync(request, cancellationToken);
            Interlocked.Increment(ref _queueDepth);

            _logger.LogDebug(
                "Enqueued processing request. OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}, QueueDepth: {QueueDepth}",
                request.OriginalCommand.OrchestratedFlowEntityId, 
                request.OriginalCommand.StepId, 
                request.OriginalCommand.ExecutionId,
                _queueDepth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to enqueue processing request. OrchestratedFlowEntityId: {OrchestratedFlowEntityId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                request.OriginalCommand.OrchestratedFlowEntityId,
                request.OriginalCommand.StepId,
                request.OriginalCommand.ExecutionId);
            throw;
        }
    }

    public int GetQueueDepth()
    {
        return _queueDepth;
    }

    /// <summary>
    /// Internal method to decrement queue depth when items are processed
    /// Called by the background service
    /// </summary>
    internal void DecrementQueueDepth()
    {
        Interlocked.Decrement(ref _queueDepth);
    }
}
