using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plugin.Shared.Interfaces;
using Plugin.Shared.Utilities;
using Plugin.Standardizer.Interfaces;
using Plugin.Standardizer.Models;
using Plugin.Standardizer.Services;
using Processor.Base.Interfaces;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.Models;

namespace Plugin.Standardizer;

/// <summary>
/// Standardizer plugin for processing audio and information content pairs from compressed files
/// Expects exactly 1 compressed file containing 2 extracted files: one audio file and one information content file
///
/// Processing:
/// - Compressed file: Extracts audio and information content files from extractedFileCacheDataObject
/// - Information content: Standardized to metadata format using MetadataImplementationType
/// - Audio files: Not processed (passed through unchanged)
///
/// Configuration:
/// - MetadataImplementationType: Mandatory - specifies the custom implementation to use (from current assembly)
/// </summary>
public class StandardizerPlugin : IPlugin
{
    private readonly ILogger<StandardizerPlugin> _logger;
    private readonly IStandardizerPluginMetricsService _metricsService;

    // Cached implementation
    private IMetadataStandardizationImplementation? _metadataImplementation;

    /// <summary>
    /// Constructor with dependency injection using standardized plugin pattern
    /// Aligned with PreFileReaderPlugin architecture
    /// </summary>
    public StandardizerPlugin(
        string pluginCompositeKey,
        ILogger<StandardizerPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create metrics service with composite key (same pattern as PreFileReaderPlugin)
        _metricsService = new StandardizerPluginMetricsService(pluginCompositeKey, _logger);

        _logger.LogInformation(
            "StandardizerPlugin initialized with composite key: {CompositeKey}",
            pluginCompositeKey);
    }

    /// <summary>
    /// Process activity data - standardize cache data from FileReader
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
            // 1. Extract configuration from DeliveryAssignmentModel
            var deliveryAssignment = entities.OfType<DeliveryAssignmentModel>().FirstOrDefault() ??
                throw new InvalidOperationException("DeliveryAssignmentModel not found in entities. StandardizerPlugin expects a DeliveryAssignmentModel for configuration.");

            var config = await ExtractConfigurationFromDeliveryAssignmentAsync(deliveryAssignment, _logger);

            // 2. Validate entities collection - must have at least one AssignmentModel
            var assignment = entities.FirstOrDefault() ??
                throw new InvalidOperationException("AssignmentModel not found in entities. StandardizerPlugin expects at least one AssignmentModel.");

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with AssignmentModel: {AssignmentName} (EntityId: {EntityId})",
                entities.Count, assignment.Name, assignment.EntityId);

            // 3. Parse input data (array of cache objects from FileReader)
            var cacheDataArray = await ParseInputCacheDataAsync(inputData, _logger);

            // 4. Validate that we have exactly one compressed file with extracted content
            if (cacheDataArray.Length != 1)
            {
                throw new InvalidOperationException($"StandardizerPlugin expects exactly 1 compressed file with extracted content, but received {cacheDataArray.Length} files");
            }

            // 5. Extract the compressed file data and validate extracted content
            var compressedFileData = cacheDataArray[0];
            if (!compressedFileData.TryGetProperty("extractedFileCacheDataObject", out var extractedFilesElement))
            {
                throw new InvalidOperationException("Compressed file data must contain 'extractedFileCacheDataObject' property");
            }

            var extractedFiles = extractedFilesElement.EnumerateArray().ToArray();
            if (extractedFiles.Length != 2)
            {
                throw new InvalidOperationException($"StandardizerPlugin expects exactly 2 extracted files (audio + information content), but found {extractedFiles.Length} extracted files");
            }

            _logger.LogInformationWithCorrelation(
                "Successfully extracted {ExtractedFileCount} files from compressed archive for processing",
                extractedFiles.Length);

            // 6. Process the extracted files from compressed archive
            var result = await ProcessExtractedFilesAsync(
                extractedFiles, config, processorId, orchestratedFlowEntityId, stepId,
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
                "Standardizer plugin processing failed - ProcessorId: {ProcessorId}, StepId: {StepId}, Duration: {Duration}ms",
                processorId, stepId, processingDuration.TotalMilliseconds);

            // Return error result
            return new[] { new ProcessedActivityData
            {
                Result = $"Error in Standardizer plugin processing: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                ExecutionId = executionId,
                ProcessorName = "StandardizerPlugin",
                Version = "1.0",
                Data = new { } // Empty object for errors
            } };
        }
    }

    /// <summary>
    /// Extract standardization configuration from DeliveryAssignmentModel payload
    /// </summary>
    private Task<StandardizationConfiguration> ExtractConfigurationFromDeliveryAssignmentAsync(
        DeliveryAssignmentModel deliveryAssignment,
        ILogger logger)
    {
        logger.LogDebugWithCorrelation("Extracting configuration from DeliveryAssignmentModel. EntityId: {EntityId}, Name: {Name}",
            deliveryAssignment.EntityId, deliveryAssignment.Name);

        if (string.IsNullOrEmpty(deliveryAssignment.Payload))
        {
            throw new InvalidOperationException("DeliveryAssignmentModel.Payload (configuration JSON) cannot be empty");
        }

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
        var config = new StandardizationConfiguration
        {
            MetadataImplementationType = JsonConfigurationExtractor.GetStringValue(root, "metadataImplementationType", "")
        };

        logger.LogInformationWithCorrelation(
            "Extracted standardization configuration from DeliveryAssignmentModel - MetadataImplementationType: {MetadataImplementationType}",
            config.MetadataImplementationType ?? "default");

        return Task.FromResult(config);
    }

    /// <summary>
    /// Load metadata implementation dynamically based on configuration
    /// </summary>
    private IMetadataStandardizationImplementation LoadMetadataImplementation(StandardizationConfiguration config, ILogger logger)
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
                logger.LogInformationWithCorrelation("No MetadataImplementationType specified, using default ExampleXmlMetadataStandardizer");
                _metadataImplementation = new Examples.ExampleXmlMetadataStandardizer();
            }
            else
            {
                logger.LogInformationWithCorrelation($"Loading metadata implementation: {implementationType}");
                var implementationLoader = new Services.ImplementationLoader(_logger);
                _metadataImplementation = implementationLoader.LoadImplementation<IMetadataStandardizationImplementation>(implementationType);
            }

            logger.LogInformationWithCorrelation($"Successfully loaded metadata implementation: {_metadataImplementation.GetType().Name}");
            return _metadataImplementation;
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, $"Failed to load metadata implementation: {config.MetadataImplementationType}. Using default implementation.");
            _metadataImplementation = new Examples.ExampleXmlMetadataStandardizer();
            return _metadataImplementation;
        }
    }

    /// <summary>
    /// Parse input data containing array of compressed file cache objects from FileReader
    /// </summary>
    private async Task<JsonElement[]> ParseInputCacheDataAsync(string inputData, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Parsing input compressed file cache data array from FileReader");

        if (string.IsNullOrWhiteSpace(inputData))
        {
            throw new InvalidOperationException("Input data is empty - Standardizer expects compressed file cache data array from FileReader");
        }

        try
        {
            // Parse as JSON array (Standardizer processes array of compressed file cache data from FileReader)
            var jsonArray = JsonSerializer.Deserialize<JsonElement[]>(inputData) ??
                throw new InvalidOperationException("Failed to deserialize input data as JSON array");

            logger.LogInformationWithCorrelation($"Successfully parsed compressed file cache data array with {jsonArray.Length} items");
            return await Task.FromResult(jsonArray);
        }
        catch (JsonException ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to parse input cache data as JSON array");
            throw new InvalidOperationException("Invalid JSON array format in input data", ex);
        }
    }

    /// <summary>
    /// Process extracted files from compressed archive with specialized handling
    /// </summary>
    private async Task<ProcessedActivityData> ProcessExtractedFilesAsync(
        JsonElement[] cacheDataArray,
        StandardizationConfiguration config,
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
            logger.LogInformationWithCorrelation("Processing extracted files for standardization");

            // 1. Load metadata implementation dynamically to get mandatory file extension
            var metadataImplementation = LoadMetadataImplementation(config, logger);
            var mandatoryExtension = metadataImplementation.MandatoryFileExtension.ToLowerInvariant();

            logger.LogInformationWithCorrelation("Looking for files with mandatory extension: {MandatoryExtension}", mandatoryExtension);

            // 2. Process all files, standardizing those with mandatory extension
            var resultData = new List<object>();
            var standardizedCount = 0;

            foreach (var cacheData in cacheDataArray)
            {
                var fileName = PluginHelper.GetFileNameFromCacheData(cacheData);

                if (fileName.EndsWith(mandatoryExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // Standardize this file (already includes extractedFileCacheDataObject from ProcessInformationContentStandardizationAsync)
                    logger.LogInformationWithCorrelation("Standardizing file: {FileName}", fileName);
                    var standardizedCacheData = await ProcessInformationContentStandardizationAsync(
                        cacheData, fileName, config,
                        processorId, orchestratedFlowEntityId, stepId, correlationId, logger);

                    resultData.Add(standardizedCacheData);
                    standardizedCount++;
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

            // Record standardization metrics
            _metricsService.RecordDataStandardization(
                recordsProcessed: cacheDataArray.Length,
                recordsSuccessful: standardizedCount,
                standardizationDuration: processingDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            // Log results
            if (standardizedCount > 0)
            {
                logger.LogInformationWithCorrelation(
                    "Successfully standardized {StandardizedCount} file(s) to XML - Duration: {Duration}ms",
                    standardizedCount, processingDuration.TotalMilliseconds);
            }
            else
            {
                logger.LogWarningWithCorrelation(
                    "No files with mandatory extension {MandatoryExtension} found for standardization",
                    mandatoryExtension);
            }

            return new ProcessedActivityData
            {
                Result = $"Successfully processed {cacheDataArray.Length} files, standardized {standardizedCount} files",
                Status = ActivityExecutionStatus.Completed,
                ExecutionId = executionId,
                ProcessorName = "StandardizerPlugin",
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
                ProcessorName = "StandardizerPlugin",
                Version = "1.0",
                Data = new { }
            };
        }
    }

    /// <summary>
    /// Process information content standardization - convert content to XML metadata
    /// Virtual method to allow customization in derived classes
    /// Throws exceptions on failure instead of returning null for better error handling
    /// </summary>
    protected virtual async Task<object> ProcessInformationContentStandardizationAsync(
        JsonElement informationFile,
        string fileName,
        StandardizationConfiguration config,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid correlationId,
        ILogger logger)
    {
        var standardizationStart = DateTime.UtcNow;

        logger.LogDebugWithCorrelation($"Starting information content standardization for: {fileName}");

        // Use the already loaded metadata implementation
        if (_metadataImplementation == null)
        {
            throw new InvalidOperationException("Metadata implementation not loaded. This should not happen.");
        }

        logger.LogInformationWithCorrelation($"Using metadata implementation: {_metadataImplementation.GetType().Name}");

        // Extract information content as string
        var informationContent = ExtractInformationContentAsString(informationFile);

        // Get complete file cache data object with XML content from implementation
        var xmlFileCacheDataObject = await _metadataImplementation.StandardizeToMetadataAsync(
            informationContent, fileName, config, logger);

        var processingDuration = DateTime.UtcNow - standardizationStart;
        logger.LogInformationWithCorrelation($"Successfully standardized information content for: {fileName} - Duration: {processingDuration.TotalMilliseconds}ms");

        // Return the complete file cache data object directly
        return xmlFileCacheDataObject;
    }

    /// <summary>
    /// Extract information content as string from cache data
    /// </summary>
    private string ExtractInformationContentAsString(JsonElement informationFile)
    {
        try
        {
            var content = PluginHelper.ExtractFileContent(informationFile);
            if (content == null)
            {
                return string.Empty;
            }

            // If content is already a string, return it
            if (content is string stringContent)
            {
                return stringContent;
            }

            // If content is a JsonElement, try to extract text from it
            if (content is JsonElement jsonElement)
            {
                // Try to get text content from binary data
                if (jsonElement.TryGetProperty("binaryData", out var binaryDataElement) &&
                    jsonElement.TryGetProperty("encoding", out var encodingElement))
                {
                    var binaryData = binaryDataElement.GetString();
                    var encoding = encodingElement.GetString();

                    if (!string.IsNullOrEmpty(binaryData) && encoding?.ToLowerInvariant() == "base64")
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(binaryData);
                            return System.Text.Encoding.UTF8.GetString(bytes);
                        }
                        catch
                        {
                            // Fall back to JSON serialization
                        }
                    }
                }
            }

            // Fall back to JSON serialization
            return JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return string.Empty;
        }
    }

}
