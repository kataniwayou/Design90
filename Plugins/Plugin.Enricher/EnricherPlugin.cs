using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plugin.Enricher.Interfaces;
using Plugin.Enricher.Models;
using Plugin.Enricher.Services;
using Plugin.Shared.Interfaces;
using Plugin.Shared.Utilities;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.Models;

namespace Plugin.Enricher;

/// <summary>
/// Enricher plugin implementation that enriches information files with additional analysis from compressed files
/// Processes compressed file cache data from StandardizerPlugin and outputs enriched cache objects with same schema
/// Expects exactly 1 compressed file containing 2 extracted files: one audio file and one information file (XML, JSON, TXT, etc.)
/// Demonstrates configurable metadata enrichment with IMetadataStandardizationImplementation pattern
///
/// Processing:
/// - Compressed file: Extracts audio and information files from extractedFileCacheDataObject
/// - Information files: Enriched using configurable implementation with StandardizationConfiguration
/// - Audio files: Passed through unchanged (no processing needed)
/// - Output: Returns array of 2 individual file cache objects (information enriched, audio unchanged)
///
/// Extensibility Features:
/// - IMetadataStandardizationImplementation interface for configurable enrichment logic
/// - StandardizationConfiguration support for runtime configuration
/// - Non-blocking enrichment failures for robust processing
/// </summary>
public class EnricherPlugin : IPlugin
{
    private readonly ILogger<EnricherPlugin> _logger;
    private readonly IEnricherPluginMetricsService _metricsService;

    // Cached implementation
    private IMetadataEnrichmentImplementation? _metadataImplementation;

    /// <summary>
    /// Constructor with dependency injection using standardized plugin pattern
    /// Aligned with StandardizerPlugin architecture
    /// </summary>
    public EnricherPlugin(
        string pluginCompositeKey,
        ILogger<EnricherPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create metrics service with composite key (same pattern as StandardizerPlugin)
        _metricsService = new EnricherPluginMetricsService(pluginCompositeKey, _logger);

        _logger.LogInformation(
            "EnricherPlugin initialized with composite key: {CompositeKey}",
            pluginCompositeKey);
    }

    /// <summary>
    /// Process activity data - enrich cache data from StandardizerPlugin
    /// </summary>
    public async Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var processingStart = DateTime.UtcNow;

        try
        {
            // 1. Extract assignment model and configuration
            var deliveryAssignment = entities.OfType<DeliveryAssignmentModel>().FirstOrDefault() ??
                throw new InvalidOperationException("DeliveryAssignmentModel not found in entities. EnricherPlugin expects at least one DeliveryAssignmentModel.");

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with DeliveryAssignmentModel: {AssignmentName} (EntityId: {EntityId})",
                entities.Count, deliveryAssignment.Name, deliveryAssignment.EntityId);

            // 2. Extract configuration from DeliveryAssignmentModel (fresh every time - stateless)
            var config = await ExtractConfigurationFromDeliveryAssignmentAsync(deliveryAssignment, _logger);

            // 3. Validate configuration
            await ValidateConfigurationAsync(config, _logger);

            // 4. Parse input data (array of individual files from StandardizerPlugin)
            var cacheDataArray = await ParseInputCacheDataAsync(inputData, _logger);

            // 5. Validate that we have exactly 2 individual files (information + audio)
            if (cacheDataArray.Length != 2)
            {
                throw new InvalidOperationException($"EnricherPlugin expects exactly 2 individual files (information + audio), but received {cacheDataArray.Length} files");
            }

            _logger.LogInformationWithCorrelation(
                "Successfully received {FileCount} individual files for enrichment",
                cacheDataArray.Length);

            // 6. Process the extracted files (information + audio)
            var result = await ProcessExtractedFilesAsync(
                cacheDataArray, config, processorId, orchestratedFlowEntityId, stepId,
                executionId, correlationId, _logger, cancellationToken);

            return new[] { result };
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
                "Enricher plugin processing failed - ProcessorId: {ProcessorId}, StepId: {StepId}, Duration: {Duration}ms",
                processorId, stepId, processingDuration.TotalMilliseconds);

            // Return error result
            return new List<ProcessedActivityData>
            {
                new ProcessedActivityData
                {
                    Result = $"Error in Enricher plugin processing: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    ExecutionId = executionId,
                    ProcessorName = "EnricherPlugin",
                    Version = "1.0",
                    Data = new { } // Empty object for errors
                }
            };
        }
    }

    /// <summary>
    /// Parse input data containing array of compressed file cache objects from StandardizerPlugin
    /// </summary>
    private async Task<JsonElement[]> ParseInputCacheDataAsync(string inputData, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Parsing input compressed file cache data array from StandardizerPlugin");

        if (string.IsNullOrWhiteSpace(inputData))
        {
            throw new InvalidOperationException("Input data is empty - Enricher expects compressed file cache data array from StandardizerPlugin");
        }

        try
        {
            // Parse as JSON array (Enricher processes array of cache data from StandardizerPlugin)
            var jsonArray = JsonSerializer.Deserialize<JsonElement[]>(inputData) ??
                throw new InvalidOperationException("Failed to deserialize input data as JSON array");

            logger.LogInformationWithCorrelation($"Successfully parsed cache data array with {jsonArray.Length} items");
            return await Task.FromResult(jsonArray);
        }
        catch (JsonException ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to parse input cache data as JSON array");
            throw new InvalidOperationException("Invalid JSON array format in input data", ex);
        }
    }

    /// <summary>
    /// Process extracted files from StandardizerPlugin with specialized handling
    /// </summary>
    private async Task<ProcessedActivityData> ProcessExtractedFilesAsync(
        JsonElement[] cacheDataArray,
        EnrichmentConfiguration config,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processingStart = DateTime.UtcNow;

        try
        {
            logger.LogInformationWithCorrelation("Processing extracted files for enrichment");

            // 1. Load metadata implementation dynamically to get mandatory file extension
            var metadataImplementation = LoadMetadataImplementation(config, logger);
            var mandatoryExtension = metadataImplementation.MandatoryFileExtension.ToLowerInvariant();

            logger.LogInformationWithCorrelation("Looking for files with mandatory extension: {MandatoryExtension}", mandatoryExtension);

            // 2. Process all files, enriching those with mandatory extension
            var resultData = new List<object>();
            var enrichedCount = 0;

            foreach (var cacheData in cacheDataArray)
            {
                var fileName = PluginHelper.GetFileNameFromCacheData(cacheData);

                if (fileName.EndsWith(mandatoryExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // Enrich this file (already includes extractedFileCacheDataObject from ProcessInformationFileEnrichmentAsync)
                    logger.LogInformationWithCorrelation("Enriching file: {FileName}", fileName);
                    var enrichedCacheData = await ProcessInformationFileEnrichmentAsync(
                        cacheData, fileName, config,
                        processorId, orchestratedFlowEntityId, stepId, correlationId, logger);

                    resultData.Add(enrichedCacheData);
                    enrichedCount++;
                }
                else
                {
                    // Keep all other files unchanged but add extractedFileCacheDataObject
                    logger.LogInformationWithCorrelation("Keeping file unchanged: {FileName}", fileName);

                    // Create schema-compliant file object with extractedFileCacheDataObject for non-mandatory files
                    var fileWithExtracted = new
                    {
                        fileCacheDataObject = cacheData.GetProperty("fileCacheDataObject"),
                        extractedFileCacheDataObject = new object[0] // Empty array as required
                    };

                    resultData.Add(fileWithExtracted);
                }
            }

            var processingDuration = DateTime.UtcNow - processingStart;

            // Record enrichment metrics
            _metricsService.RecordDataEnrichment(
                recordsProcessed: cacheDataArray.Length,
                recordsSuccessful: enrichedCount,
                enrichmentDuration: processingDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            // Log results
            if (enrichedCount > 0)
            {
                logger.LogInformationWithCorrelation(
                    "Successfully enriched {EnrichedCount} file(s) with metadata - Duration: {Duration}ms",
                    enrichedCount, processingDuration.TotalMilliseconds);
            }
            else
            {
                logger.LogWarningWithCorrelation(
                    "No files with mandatory extension {MandatoryExtension} found for enrichment",
                    mandatoryExtension);
            }

            return new ProcessedActivityData
            {
                Result = $"Successfully processed {cacheDataArray.Length} files, enriched {enrichedCount} files",
                Status = ActivityExecutionStatus.Completed,
                ExecutionId = executionId,
                ProcessorName = "EnricherPlugin",
                Version = "1.0",
                Data = resultData.ToArray()
            };
        }
        catch (Exception ex)
        {
            // Record plugin exception for processing failure
            _metricsService.RecordPluginException(
                exceptionType: ex.GetType().Name,
                severity: "error",
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            logger.LogErrorWithCorrelation(ex, "Failed to process audio and information content pair");

            return new ProcessedActivityData
            {
                Result = $"Audio and information content pair processing error: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                ExecutionId = executionId,
                ProcessorName = "EnricherPlugin",
                Version = "1.0",
                Data = new { }
            };
        }
    }

    /// <summary>
    /// Process information file enrichment using configurable implementation
    /// Throws exceptions on failure instead of returning null for better error handling
    /// </summary>
    protected virtual async Task<object> ProcessInformationFileEnrichmentAsync(
        JsonElement informationFile,
        string fileName,
        EnrichmentConfiguration config,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid correlationId,
        ILogger logger)
    {
        var enrichmentStart = DateTime.UtcNow;

        logger.LogDebugWithCorrelation($"Starting information file enrichment for: {fileName}");

        // Use the already loaded metadata implementation
        if (_metadataImplementation == null)
        {
            throw new InvalidOperationException("Metadata implementation not loaded. This should not happen.");
        }

        logger.LogInformationWithCorrelation($"Using metadata implementation: {_metadataImplementation.GetType().Name}");

        // Extract information content as string
        var informationContent = ExtractInformationContentAsString(informationFile);

        // Get complete file cache data object with enriched content from implementation
        var enrichedFileCacheDataObject = await _metadataImplementation.EnrichToMetadataAsync(
            informationContent, fileName, config, logger);

        var processingDuration = DateTime.UtcNow - enrichmentStart;
        logger.LogInformationWithCorrelation($"Successfully enriched information content for: {fileName} - Duration: {processingDuration.TotalMilliseconds}ms");

        // Return the complete file cache data object directly
        return enrichedFileCacheDataObject;
    }

    /// <summary>
    /// Extract information content as string from file content object
    /// </summary>
    private static string ExtractInformationContentAsString(JsonElement informationFile)
    {
        try
        {
            if (informationFile.TryGetProperty("fileCacheDataObject", out var fileCacheObj) &&
                fileCacheObj.TryGetProperty("fileContent", out var content))
            {
                // Try to get standardized text content first
                if (content.TryGetProperty("standardizedTextContent", out var standardizedContent))
                {
                    return standardizedContent.GetString() ?? "";
                }

                // Fallback to binary data if available
                if (content.TryGetProperty("binaryData", out var binaryData))
                {
                    var binaryDataString = binaryData.GetString();
                    if (!string.IsNullOrEmpty(binaryDataString))
                    {
                        var bytes = Convert.FromBase64String(binaryDataString);
                        return System.Text.Encoding.UTF8.GetString(bytes);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore parsing errors and return empty string
        }

        return "";
    }

    /// <summary>
    /// Extract enrichment configuration from DeliveryAssignmentModel payload
    /// </summary>
    private static Task<EnrichmentConfiguration> ExtractConfigurationFromDeliveryAssignmentAsync(
        DeliveryAssignmentModel deliveryAssignment,
        ILogger logger)
    {
        logger.LogInformationWithCorrelation("Extracting enrichment configuration from DeliveryAssignmentModel");

        // Parse JSON using consistent JsonElement pattern
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(deliveryAssignment.Payload ?? "{}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in DeliveryAssignmentModel payload: {ex.Message}", ex);
        }

        // Extract configuration using shared utilities
        var config = new EnrichmentConfiguration
        {
            MetadataImplementationType = JsonConfigurationExtractor.GetStringValue(root, "metadataImplementationType", "")
        };

        logger.LogInformationWithCorrelation(
            "Extracted enrichment configuration from DeliveryAssignmentModel - MetadataImplementationType: {MetadataImplementationType}",
            config.MetadataImplementationType ?? "default");

        return Task.FromResult(config);
    }

    /// <summary>
    /// Load metadata implementation dynamically based on configuration
    /// </summary>
    private IMetadataEnrichmentImplementation LoadMetadataImplementation(EnrichmentConfiguration config, ILogger logger)
    {
        if (_metadataImplementation != null)
        {
            return _metadataImplementation;
        }

        try
        {
            // Use configured implementation type or default
            var implementationType = config.MetadataImplementationType;

            if (string.IsNullOrEmpty(implementationType))
            {
                logger.LogInformationWithCorrelation("No MetadataImplementationType specified, using default ExampleXmlMetadataEnricher");
                _metadataImplementation = new Examples.ExampleXmlMetadataEnricher();
            }
            else
            {
                logger.LogInformationWithCorrelation($"Loading metadata implementation: {implementationType}");
                var implementationLoader = new Services.ImplementationLoader(_logger);
                _metadataImplementation = implementationLoader.LoadImplementation<IMetadataEnrichmentImplementation>(implementationType);
            }

            logger.LogInformationWithCorrelation($"Successfully loaded metadata implementation: {_metadataImplementation.GetType().Name}");
            return _metadataImplementation;
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, $"Failed to load metadata implementation: {config.MetadataImplementationType}. Using default implementation.");
            _metadataImplementation = new Examples.ExampleXmlMetadataEnricher();
            return _metadataImplementation;
        }
    }

    /// <summary>
    /// Validate enrichment configuration
    /// </summary>
    private static Task ValidateConfigurationAsync(EnrichmentConfiguration config, ILogger logger)
    {
        logger.LogInformationWithCorrelation("Validating enrichment configuration");

        // Configuration validation is optional - plugin can work with default implementation
        if (string.IsNullOrEmpty(config.MetadataImplementationType))
        {
            logger.LogInformationWithCorrelation("No specific metadata implementation type specified, using default enrichment");
        }
        else
        {
            logger.LogInformationWithCorrelation($"Using metadata implementation type: {config.MetadataImplementationType}");
        }

        return Task.CompletedTask;
    }
}
