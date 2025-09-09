using Shared.Models;

namespace Plugin.Shared.Interfaces;

/// <summary>
/// Interface for plugin implementations that can be dynamically loaded and executed
/// by the PluginLoaderProcessor. Plugin must implement this interface to be compatible
/// with the plugin loading system.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Processes activity data with the same signature as BaseProcessorApplication.ProcessActivityDataAsync
    /// This method will be called by the PluginLoaderProcessor to execute the plugin's business logic
    /// </summary>
    /// <param name="processorId">ID of the processor executing the activity</param>
    /// <param name="orchestratedFlowEntityId">ID of the orchestrated flow entity</param>
    /// <param name="stepId">ID of the step being executed</param>
    /// <param name="executionId">Unique execution ID for this activity instance</param>
    /// <param name="publishId">Unique publish ID for this activity instance</param>
    /// <param name="entities">Collection of assignment entities to process</param>
    /// <param name="inputData">Input data retrieved from cache (validated against InputSchema)</param>
    /// <param name="correlationId">Correlation ID for tracking</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Collection of processed activity data that will be validated against OutputSchema and saved to cache</returns>
    Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}
