using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plugin.PreFileReader.Interfaces;
using Plugin.PreFileReader.Models;
using Plugin.PreFileReader.Services;
using Plugin.PreFileReader.Utilities;
using Plugin.Shared.Interfaces;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.Models;
using Shared.Services.Interfaces;

namespace Plugin.PreFileReader;

/// <summary>
/// PreFileReader plugin implementation that handles compressed archives (ZIP, RAR, 7-Zip, GZIP, TAR)
/// Implements IPlugin interface for dynamic loading by PluginLoaderProcessor
/// </summary>
public class PreFileReaderPlugin : IPlugin
{
    private readonly ILogger<PreFileReaderPlugin> _logger;
    private readonly IPreFileReaderPluginMetricsService _metricsService;
    private readonly IFileRegistrationService _fileRegistrationService;



    /// <summary>
    /// Constructor with dependency injection using standardized plugin pattern
    /// </summary>
    public PreFileReaderPlugin(
        string pluginCompositeKey,
        ILogger<PreFileReaderPlugin> logger,
        ICacheService cacheService)
    {
        // Store host-provided services with null check
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Validate plugin composite key
        if (string.IsNullOrWhiteSpace(pluginCompositeKey))
            throw new ArgumentException("Plugin composite key cannot be null or empty", nameof(pluginCompositeKey));

        // Create metrics service with DI-provided composite key
        _metricsService = new PreFileReaderPluginMetricsService(pluginCompositeKey, _logger);

        // Create file registration service with plugin logger
        var cacheConfig = Options.Create(new FileRegistrationCacheConfiguration());
        _fileRegistrationService = new FileRegistrationService(cacheService, _logger, cacheConfig);
    }

    /// <summary>
    /// Plugin implementation of ProcessActivityDataAsync
    /// Handles both discovery phase (executionId empty) and processing phase (executionId populated)
    /// </summary>
    public async Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData, // Discovery: original config | Processing: cached file path
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var processingStart = DateTime.UtcNow;

        _logger.LogInformationWithCorrelation(
            "Starting PreFileReader plugin processing - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
            processorId, stepId, executionId);

        try
        {
            // 1. Validate entities collection - must have at least one AddressAssignmentModel
            var addressAssignment = entities.OfType<AddressAssignmentModel>().FirstOrDefault();
            if (addressAssignment == null)
            {
                throw new InvalidOperationException("AddressAssignmentModel not found in entities. PreFileReaderPlugin expects at least one AddressAssignmentModel.");
            }

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with AddressAssignmentModel: {AddressName} (EntityId: {EntityId})",
                entities.Count, addressAssignment.Name, addressAssignment.EntityId);

            // 1. Extract configuration from AddressAssignmentModel (fresh every time - stateless)
            var config = await ExtractConfigurationFromAddressAssignmentAsync(addressAssignment, _logger);

            // 2. Validate configuration
            await ValidateConfigurationAsync(config, _logger);

            return await ProcessFileDiscoveryAsync(
                    config, entities, processorId, orchestratedFlowEntityId, stepId,
                    executionId, correlationId, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;

            // Record plugin exception
            _metricsService.RecordPluginException(
                exceptionType: ex.GetType().Name,
                severity: "error",
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            _logger.LogErrorWithCorrelation(ex,
                "PreFileReader plugin processing failed - ProcessorId: {ProcessorId}, StepId: {StepId}, Duration: {Duration}ms",
                processorId, stepId, processingDuration.TotalMilliseconds);

            // Return error result
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in PreFileReader plugin processing: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "PreFileReaderProcessor", // Keep same name for compatibility
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    /// <summary>
    /// Process file discovery phase - discover files and cache them for individual processing
    /// </summary>
    private async Task<IEnumerable<ProcessedActivityData>> ProcessFileDiscoveryAsync(
        PreFileReaderConfiguration config,
        List<AssignmentModel> entities,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformationWithCorrelation(
            "Using cache-based approach for file discovery and processing");

        try
        {
            // Use local discovery and return ProcessedActivityData for each discovered file
            var processedActivityDataList = await DiscoverAndRegisterFilesAsync(
                config,
                _fileRegistrationService,
                processorId,
                executionId,
                correlationId,
                orchestratedFlowEntityId,
                stepId,
                entities,
                logger);

            logger.LogInformationWithCorrelation(
                "File discovery phase completed - Discovered {DiscoveredFiles} files for processing",
                processedActivityDataList.Count);

            // Return ProcessedActivityData list for immediate processing
            return processedActivityDataList;
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex,
                "Failed to complete file discovery phase");

            // Return error result for discovery phase
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in file discovery phase: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "PreFileReaderProcessor",
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    private Task<PreFileReaderConfiguration> ExtractConfigurationFromAddressAssignmentAsync(
        AddressAssignmentModel addressAssignment,
        ILogger logger)
    {
        logger.LogDebugWithCorrelation("Extracting configuration from AddressAssignmentModel. EntityId: {EntityId}, Name: {Name}",
            addressAssignment.EntityId, addressAssignment.Name);

        if (string.IsNullOrEmpty(addressAssignment.Payload))
        {
            throw new InvalidOperationException("AddressAssignmentModel.Payload (configuration JSON) cannot be empty");
        }

        // Parse JSON using consistent JsonElement pattern
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(addressAssignment.Payload ?? "{}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in AddressAssignmentModel.Payload", ex);
        }

        // Extract configuration using shared utilities
        var config = new PreFileReaderConfiguration
        {
            FolderPath = addressAssignment.ConnectionString,
            SearchPattern = JsonConfigurationExtractor.GetStringValue(root, "searchPattern", "*.{zip,rar,7z,gz,tar}"),
            MaxFilesToProcess = JsonConfigurationExtractor.GetIntValue(root, "maxFilesToProcess", 50)
        };

        logger.LogInformationWithCorrelation(
            "Extracted PreFileReader configuration from AddressAssignmentModel - FolderPath: {FolderPath}, SearchPattern: {SearchPattern}, MaxFiles: {MaxFiles}",
            config.FolderPath, config.SearchPattern, config.MaxFilesToProcess);

        return Task.FromResult(config);
    }

    private Task ValidateConfigurationAsync(PreFileReaderConfiguration config, ILogger logger)
    {
        logger.LogInformationWithCorrelation("Validating PreFileReader configuration");

        if (string.IsNullOrWhiteSpace(config.FolderPath))
        {
            throw new InvalidOperationException("FolderPath cannot be empty");
        }

        if (!Directory.Exists(config.FolderPath))
        {
            throw new DirectoryNotFoundException($"Folder does not exist: {config.FolderPath}");
        }

        if (config.MaxFilesToProcess <= 0)
        {
            throw new InvalidOperationException($"MaxFilesToProcess must be greater than 0, but was {config.MaxFilesToProcess}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Discovers and registers files for processing and returns ProcessedActivityData for each file
    /// Each file gets a unique executionId generated during registration
    /// </summary>
    private async Task<List<ProcessedActivityData>> DiscoverAndRegisterFilesAsync(
        PreFileReaderConfiguration config,
        IFileRegistrationService fileRegistrationService,
        Guid processorId,
        Guid executionId,
        Guid correlationId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        List<AssignmentModel> entities,
        ILogger logger)
    {
        var scanStart = DateTime.UtcNow;
        List<string> allFiles;

        try
        {
            // Single file enumeration
            allFiles = FilePatternExpander.EnumerateFiles(config.FolderPath, config.SearchPattern).ToList();

            var scanDuration = DateTime.UtcNow - scanStart;

            // Record successful directory scan metrics
            _metricsService.RecordDirectoryScan(
                success: true,
                directoryPath: config.FolderPath,
                duration: scanDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            // Record file discovery metrics
            _metricsService.RecordFileDiscovery(
                filesFound: allFiles.Count,
                directoryPath: config.FolderPath,
                scanDuration: scanDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);
        }
        catch (Exception ex)
        {
            var scanDuration = DateTime.UtcNow - scanStart;

            // Record failed directory scan metrics
            _metricsService.RecordDirectoryScan(
                success: false,
                directoryPath: config.FolderPath,
                duration: scanDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            // Record plugin exception for directory scan failure
            _metricsService.RecordPluginException(
                exceptionType: ex.GetType().Name,
                severity: "error",
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            throw; // Re-throw the exception
        }

        // Apply MaxFilesToProcess limit if specified
        if (config.MaxFilesToProcess > 0 && allFiles.Count > config.MaxFilesToProcess)
        {
            allFiles = allFiles.Take(config.MaxFilesToProcess).ToList();
            logger.LogInformationWithCorrelation(
                "Limited file discovery to {MaxFiles} files (found {TotalFiles} total)",
                config.MaxFilesToProcess, allFiles.Count);
        }

        logger.LogInformationWithCorrelation(
            "Discovered {FileCount} files for processing", allFiles.Count);

        // Registration and ProcessedActivityData creation loop
        var processedActivityDataList = new List<ProcessedActivityData>();
        foreach (var filePath in allFiles)
        {
            // Generate unique executionId for each file processing
            var fileExecutionId = Guid.NewGuid();



            // Atomically try to register the file - returns true if successfully added, false if already registered
            var wasAdded = await fileRegistrationService.TryToAddAsync(filePath, processorId, fileExecutionId, correlationId);
            if (wasAdded)
            {
                // Create ProcessedActivityData for this file
                var processedActivityData = new ProcessedActivityData
                {
                    ExecutionId = fileExecutionId,
                    Data = filePath,
                    Status = ActivityExecutionStatus.Completed,
                    Result = $"File discovered and registered: {filePath}",
                    ProcessorName = "PreFileReaderProcessor",
                    Version = "1.0"
                };

                processedActivityDataList.Add(processedActivityData);

                logger.LogDebugWithCorrelation(
                    "Registered file and created ProcessedActivityData for: {FilePath} with ExecutionId: {ExecutionId}",
                    filePath, fileExecutionId);
            }
        }

        logger.LogInformationWithCorrelation(
            "Registered {RegisteredFiles} new files out of {TotalFiles} discovered",
            processedActivityDataList.Count, allFiles.Count);



        return processedActivityDataList;
    }


}
