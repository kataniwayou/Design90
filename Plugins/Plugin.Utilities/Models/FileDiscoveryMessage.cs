namespace Plugin.Utilities.Models;

/// <summary>
/// Message published to Kafka when a file is discovered for processing
/// Contains all necessary information for individual file processing
/// Moved to Plugin.Utilities for sharing between FilePublisher and FileConsumer
/// </summary>
public class FileDiscoveryMessage
{
    /// <summary>
    /// Full path to the discovered file
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Processor ID that discovered the file
    /// </summary>
    public Guid ProcessorId { get; set; }

    /// <summary>
    /// Orchestrated flow entity ID
    /// </summary>
    public Guid OrchestratedFlowEntityId { get; set; }

    /// <summary>
    /// Step ID in the workflow
    /// </summary>
    public Guid StepId { get; set; }

    /// <summary>
    /// Unique execution ID for this specific file processing
    /// </summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    /// Unique publish ID for this message
    /// </summary>
    public Guid PublishId { get; set; }

    /// <summary>
    /// Correlation ID for tracking across the system
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// When this message was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
