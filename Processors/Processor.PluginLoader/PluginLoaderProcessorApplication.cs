using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Processor.Base;
using Processor.PluginLoader.Models;
using Processor.PluginLoader.Services;
using Shared.Correlation;
using Shared.Models;

namespace Processor.PluginLoader;

/// <summary>
/// PluginLoader processor application that dynamically loads and executes plugins
/// based on configuration provided in each ProcessActivityDataAsync call.
/// Features two-level caching:
/// - Level 1: PluginManager instances are always cached by AssemblyBasePath
/// - Level 2: Plugin instances are cached based on IsStateless configuration flag
/// </summary>
public class PluginLoaderProcessorApplication : BaseProcessorApplication
{
    /// <summary>
    /// Configure processor-specific services
    /// </summary>
    protected override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Call base implementation
        base.ConfigureServices(services, configuration);

        // Register processor-specific services
        // Note: PluginLoaderProcessorMetricsService removed - metrics moved to individual plugins
    }

    /// <summary>
    /// Concrete implementation of the activity processing logic for file adapter pipe processing
    /// This processor acts as a pipe - it receives cache data, processes it, and passes it through
    /// while recording metrics and performing any necessary adaptations
    /// </summary>
    protected override async Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
         Guid publishId,
        List<AssignmentModel> entities,
        string inputData, // Contains cacheData from previous processor
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var logger = ServiceProvider.GetRequiredService<ILogger<PluginLoaderProcessorApplication>>();
        var processingStart = DateTime.UtcNow;

        logger.LogInformationWithCorrelation(
            "Starting plugin processing - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
            processorId, stepId, executionId);

        try
        {
            // 1. Validate entities collection - expect at least two entities with one PluginAssignmentModel
            if (entities.Count < 2)
            {
                throw new InvalidOperationException($"PluginLoaderProcessor expects at least two entities, but received {entities.Count}. At least one must be a PluginAssignmentModel.");
            }

            var pluginAssignment = entities.OfType<PluginAssignmentModel>().FirstOrDefault();
            if (pluginAssignment == null)
            {
                throw new InvalidOperationException("No PluginAssignmentModel found in entities collection. PluginLoaderProcessor expects at least one PluginAssignmentModel among the provided entities.");
            }

            logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with PluginAssignmentModel: {PluginName} (EntityId: {EntityId})",
                entities.Count, pluginAssignment.Name, pluginAssignment.EntityId);

            var config = await ExtractPluginConfigurationFromPluginAssignmentAsync(entities, logger);

            // 2. Validate plugin configuration
            await ValidatePluginConfigurationAsync(config, logger);

            // 3. Get PluginManager from factory (Level 1 cache - always cached)
            var pluginManager = PluginManagerFactory.GetPluginManager(config.AssemblyBasePath, ServiceProvider);

            // 4. Get plugin instance with conditional caching (Level 2 cache - based on IsStateless)
            var pluginVersion = Version.Parse(config.Version);
            var plugin = pluginManager.GetPluginInstance(
                config.AssemblyName, pluginVersion, config.TypeName, config.IsStateless);

            logger.LogInformationWithCorrelation(
                "Successfully loaded plugin instance: {TypeName} from {AssemblyName} v{Version} (IsStateless: {IsStateless})",
                config.TypeName, config.AssemblyName, config.Version, config.IsStateless);

            // 5. Execute plugin with timeout if configured
            IEnumerable<ProcessedActivityData> result;
            if (config.ExecutionTimeoutMs > 0 && config.ExecutionTimeoutMs != 300000) // Only apply timeout if different from default
            {
                using var timeoutCts = new CancellationTokenSource(config.ExecutionTimeoutMs);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                result = await plugin.ProcessActivityDataAsync(
                    processorId, orchestratedFlowEntityId, stepId,
                    executionId, publishId, entities, inputData,
                    correlationId, combinedCts.Token);
            }
            else
            {
                result = await plugin.ProcessActivityDataAsync(
                    processorId, orchestratedFlowEntityId, stepId,
                    executionId, publishId, entities, inputData,
                    correlationId, cancellationToken);
            }

            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogInformationWithCorrelation(
                "Completed plugin processing in {Duration}ms - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                processingDuration.TotalMilliseconds, processorId, stepId, executionId);

            return result;
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogErrorWithCorrelation(ex,
                "Error in plugin processing after {Duration}ms - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
                processingDuration.TotalMilliseconds, processorId, stepId, executionId);

            // Return error result
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in plugin processing: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { }, // Empty data on error
                    ProcessorName = "PluginLoaderProcessor",
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    /// <summary>
    /// Extract plugin configuration from PluginAssignmentModel within the entities collection
    /// </summary>
    private async Task<PluginLoaderConfiguration> ExtractPluginConfigurationFromPluginAssignmentAsync(List<AssignmentModel> entities, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Extracting plugin configuration from {EntityCount} entities", entities.Count);

        // Find the PluginAssignmentModel (validation already ensures it exists)
        var pluginAssignment = entities.OfType<PluginAssignmentModel>().First();

        logger.LogDebugWithCorrelation("Found PluginAssignmentModel. EntityId: {EntityId}, Name: {Name}",
            pluginAssignment.EntityId, pluginAssignment.Name);

        // Extract plugin configuration directly from PluginAssignmentModel properties
        var config = new PluginLoaderConfiguration
        {
            AssemblyBasePath = pluginAssignment.AssemblyBasePath,
            AssemblyName = pluginAssignment.AssemblyName,
            Version = pluginAssignment.AssemblyVersion, // Use AssemblyVersion instead of entity Version
            TypeName = pluginAssignment.TypeName,
            ExecutionTimeoutMs = pluginAssignment.ExecutionTimeoutMs,
            IsStateless = pluginAssignment.IsStateless
        };

        logger.LogInformationWithCorrelation(
            "Extracted plugin configuration from PluginAssignmentModel - AssemblyBasePath: {AssemblyBasePath}, AssemblyName: {AssemblyName}, AssemblyVersion: {AssemblyVersion}, TypeName: {TypeName}, ExecutionTimeoutMs: {ExecutionTimeoutMs}, IsStateless: {IsStateless}",
            config.AssemblyBasePath, config.AssemblyName, config.Version, config.TypeName, config.ExecutionTimeoutMs, config.IsStateless);

        return await Task.FromResult(config);
    }

    /// <summary>
    /// Validate the extracted plugin configuration
    /// </summary>
    private async Task ValidatePluginConfigurationAsync(PluginLoaderConfiguration config, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Validating plugin configuration");

        if (string.IsNullOrWhiteSpace(config.AssemblyBasePath))
        {
            throw new InvalidOperationException("AssemblyBasePath cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(config.AssemblyName))
        {
            throw new InvalidOperationException("AssemblyName cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(config.Version))
        {
            throw new InvalidOperationException("Version cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(config.TypeName))
        {
            throw new InvalidOperationException("TypeName cannot be empty");
        }

        // Validate version format
        if (!Version.TryParse(config.Version, out _))
        {
            throw new InvalidOperationException($"Invalid version format: {config.Version}");
        }

        // Validate assembly base path exists
        if (!Directory.Exists(config.AssemblyBasePath))
        {
            throw new InvalidOperationException($"Assembly base path does not exist: {config.AssemblyBasePath}");
        }

        // Validate execution timeout
        if (config.ExecutionTimeoutMs <= 0)
        {
            throw new InvalidOperationException("ExecutionTimeoutMs must be greater than 0");
        }

        logger.LogDebugWithCorrelation("Plugin configuration validation completed successfully");
        await Task.CompletedTask;
    }
}
