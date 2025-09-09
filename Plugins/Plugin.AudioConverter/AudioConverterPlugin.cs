using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plugin.AudioConverter.Interfaces;
using Plugin.AudioConverter.Models;
using Plugin.AudioConverter.Services;
using Plugin.AudioConverter.Utilities;
using Plugin.Shared.Interfaces;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.Models;

namespace Plugin.AudioConverter;

/// <summary>
/// Audio converter plugin for processing individual audio-information file pairs
/// Expects exactly 2 individual files: one audio file and one information file (XML, JSON, TXT, etc.)
///
/// Processing:
/// - Individual files: Receives 2 separate file cache objects with extractedFileCacheDataObject: []
/// - Audio files: Converted using FFmpeg with configured arguments
/// - Information files: Passed through unchanged (no processing needed)
/// - Output: Returns array of 2 individual file cache objects (information unchanged, audio converted)
///
/// Extensibility:
/// - Virtual PerformConversionAsync method for custom logic
/// - Virtual ProcessAudioFileConversionAsync method for audio processing
/// </summary>
public class AudioConverterPlugin : IPlugin
{
    private readonly ILogger<AudioConverterPlugin> _logger;
    private readonly IAudioConverterPluginMetricsService _metricsService;

    // Cached FFmpeg service implementation
    private IFFmpegService? _ffmpegService;

    // Cached audio conversion implementation
    private IAudioConversionImplementation? _audioImplementation;

    /// <summary>
    /// Constructor with dependency injection using standardized plugin pattern
    /// Aligned with StandardizerPlugin architecture
    /// </summary>
    public AudioConverterPlugin(
        string pluginCompositeKey,
        ILogger<AudioConverterPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create metrics service with composite key (same pattern as StandardizerPlugin)
        _metricsService = new AudioConverterPluginMetricsService(pluginCompositeKey, _logger);

        _logger.LogInformation(
            "AudioConverterPlugin initialized with composite key: {CompositeKey}",
            pluginCompositeKey);
    }

    /// <summary>
    /// Process activity data with audio conversion
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
        CancellationToken cancellationToken = default)
    {
        var processingStart = DateTime.UtcNow;

        try
        {
            _logger.LogInformationWithCorrelation("Starting AudioConverter plugin processing");

            // 1. Validate entities collection - must have at least one DeliveryAssignmentModel
            var deliveryAssignment = entities.OfType<DeliveryAssignmentModel>().FirstOrDefault();
            if (deliveryAssignment == null)
            {
                throw new InvalidOperationException("DeliveryAssignmentModel not found in entities. AudioConverterPlugin expects at least one DeliveryAssignmentModel.");
            }

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with DeliveryAssignmentModel: {DeliveryName} (EntityId: {EntityId})",
                entities.Count, deliveryAssignment.Name, deliveryAssignment.EntityId);

            // 1. Extract configuration from DeliveryAssignmentModel (fresh every time - stateless)
            var config = await ExtractConfigurationFromDeliveryAssignmentAsync(deliveryAssignment, _logger);

            // 2. Validate configuration
            await ValidateConfigurationAsync(config, _logger);

            // 3. Parse input data (array of individual files from EnricherPlugin)
            var cacheDataArray = await ParseInputCacheDataAsync(inputData, _logger);

            // 4. Validate that we have exactly 2 individual files (information + audio)
            if (cacheDataArray.Length != 2)
            {
                throw new InvalidOperationException($"AudioConverterPlugin expects exactly 2 individual files (information + audio), but received {cacheDataArray.Length} files");
            }

            _logger.LogInformationWithCorrelation(
                "Successfully received {FileCount} individual files for audio conversion",
                cacheDataArray.Length);

            // 5. Process the extracted files (information + audio)
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

            _logger.LogErrorWithCorrelation(ex, "Failed to process entities in AudioConverter plugin");

            // Return error result
            return new[] { new ProcessedActivityData
            {
                Result = $"Error in AudioConverter plugin processing: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                ExecutionId = executionId,
                ProcessorName = "AudioConverterPlugin",
                Version = "1.0",
                Data = new { } // Empty object for errors
            } };
        }
    }

    private Task<AudioConverterConfiguration> ExtractConfigurationFromDeliveryAssignmentAsync(
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
        var config = new AudioConverterConfiguration
        {
            ConversionArguments = JsonConfigurationExtractor.GetStringValue(root, "ffmpegConversionArguments", "-acodec libmp3lame -ab 320k -ar 44100 -ac 2"),
            FFmpegPath = JsonConfigurationExtractor.GetStringValue(root, "ffmpegPath", null!),
            MetadataImplementationType = JsonConfigurationExtractor.GetStringValue(root, "metadataImplementationType", null!)
        };

        logger.LogInformationWithCorrelation(
            "Extracted AudioConverter configuration - ConversionArguments: {ConversionArguments}, FFmpegPath: {FFmpegPath}",
            config.ConversionArguments, config.FFmpegPath ?? "system PATH");

        return Task.FromResult(config);
    }

    private Task ValidateConfigurationAsync(AudioConverterConfiguration configuration, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Validating AudioConverter configuration");

        if (string.IsNullOrWhiteSpace(configuration.ConversionArguments))
        {
            throw new InvalidOperationException("FFmpeg conversion arguments are required");
        }

        // Create FFmpeg service if not already created
        _ffmpegService ??= new FFmpegService(_logger);

        // Validate FFmpeg availability at configured path
        if (!_ffmpegService.IsFFmpegAvailable(configuration.FFmpegPath))
        {
            var pathInfo = configuration.FFmpegPath ?? "system PATH";
            throw new InvalidOperationException($"FFmpeg is not available at: {pathInfo}");
        }

        logger.LogInformationWithCorrelation("AudioConverter configuration validation completed successfully");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Load audio implementation dynamically based on configuration
    /// </summary>
    private IAudioConversionImplementation LoadAudioImplementation(AudioConverterConfiguration config, ILogger logger)
    {
        if (_audioImplementation != null)
        {
            return _audioImplementation;
        }

        try
        {
            // Use configured implementation type or default
            var implementationType = config.MetadataImplementationType;

            if (string.IsNullOrEmpty(implementationType))
            {
                logger.LogInformationWithCorrelation("No MetadataImplementationType specified, using default ExampleAudioConverter");
                _audioImplementation = new Examples.ExampleAudioConverter();
            }
            else
            {
                logger.LogInformationWithCorrelation($"Loading audio implementation: {implementationType}");
                var implementationLoader = new Services.ImplementationLoader(_logger);
                _audioImplementation = implementationLoader.LoadImplementation<IAudioConversionImplementation>(implementationType);
            }

            logger.LogInformationWithCorrelation($"Successfully loaded audio implementation: {_audioImplementation.GetType().Name}");
            return _audioImplementation;
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, $"Failed to load audio implementation: {config.MetadataImplementationType}. Using default implementation.");
            _audioImplementation = new Examples.ExampleAudioConverter();
            return _audioImplementation;
        }
    }

    /// <summary>
    /// Parse input compressed file cache data from JSON string
    /// </summary>
    private static async Task<JsonElement[]> ParseInputCacheDataAsync(string inputData, ILogger logger)
    {
        try
        {
            logger.LogDebugWithCorrelation("Parsing input compressed file cache data array");

            var jsonArray = JsonSerializer.Deserialize<JsonElement[]>(inputData) ??
                throw new InvalidOperationException("Failed to deserialize input data as JSON array");

            logger.LogInformationWithCorrelation($"Successfully parsed {jsonArray.Length} compressed file cache data items");
            return await Task.FromResult(jsonArray);
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to parse input cache data");
            throw;
        }
    }

    /// <summary>
    /// Process extracted files from EnricherPlugin with specialized handling
    /// </summary>
    private async Task<ProcessedActivityData> ProcessExtractedFilesAsync(
        JsonElement[] cacheDataArray,
        AudioConverterConfiguration config,
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
            logger.LogInformationWithCorrelation("Processing extracted files for audio conversion");

            // 1. Load audio implementation dynamically to get mandatory file extension
            var audioImplementation = LoadAudioImplementation(config, logger);
            var mandatoryExtension = audioImplementation.MandatoryFileExtension.ToLowerInvariant();

            logger.LogInformationWithCorrelation("Looking for files with mandatory extension: {MandatoryExtension}", mandatoryExtension);

            // 2. Process all files, converting those with mandatory extension
            var resultData = new List<object>();
            var convertedCount = 0;

            foreach (var cacheData in cacheDataArray)
            {
                var fileName = AudioConverterHelper.GetFileNameFromCacheData(cacheData);

                if (fileName.EndsWith(mandatoryExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // Convert this file (already includes extractedFileCacheDataObject from ProcessAudioFileConversionAsync)
                    logger.LogInformationWithCorrelation("Converting audio file: {FileName}", fileName);
                    var convertedCacheData = await ProcessAudioFileConversionAsync(cacheData, fileName, config, logger);
                    resultData.Add(convertedCacheData);
                    convertedCount++;
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

            // Record conversion metrics
            _metricsService.RecordDataConversion(
                recordsProcessed: cacheDataArray.Length,
                recordsSuccessful: convertedCount,
                conversionDuration: processingDuration,
                correlationId: correlationId.ToString(),
                orchestratedFlowEntityId: orchestratedFlowEntityId,
                stepId: stepId,
                executionId: executionId);

            // Log results
            if (convertedCount > 0)
            {
                logger.LogInformationWithCorrelation(
                    "Successfully converted {ConvertedCount} audio file(s) to target format - Duration: {Duration}ms",
                    convertedCount, processingDuration.TotalMilliseconds);
            }
            else
            {
                logger.LogWarningWithCorrelation(
                    "No files with mandatory extension {MandatoryExtension} found for conversion",
                    mandatoryExtension);
            }

            return new ProcessedActivityData
            {
                Result = $"Successfully processed {cacheDataArray.Length} files, converted {convertedCount} files",
                Status = ActivityExecutionStatus.Completed,
                ExecutionId = executionId,
                ProcessorName = "AudioConverterPlugin",
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
                Result = $"Audio and information content pair conversion error: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                ExecutionId = executionId,
                ProcessorName = "AudioConverterPlugin",
                Version = "1.0",
                Data = new { }
            };
        }
    }

    /// <summary>
    /// Process audio file conversion using FFmpeg
    /// Virtual method to allow customization in derived classes
    /// Throws exceptions on failure instead of returning null for better error handling
    /// </summary>
    protected virtual async Task<object> ProcessAudioFileConversionAsync(
        JsonElement audioFile,
        string fileName,
        AudioConverterConfiguration config,
        ILogger logger)
    {
        var conversionStart = DateTime.UtcNow;

        logger.LogDebugWithCorrelation($"Starting audio file conversion for: {fileName}");

        // Extract audio binary data from the file
        var audioData = AudioConverterHelper.ExtractAudioBinaryData(audioFile) ??
            throw new InvalidOperationException("Failed to extract audio binary data from file");

        // Use the already loaded audio implementation
        if (_audioImplementation == null)
        {
            throw new InvalidOperationException("Audio implementation not loaded. This should not happen.");
        }

        logger.LogInformationWithCorrelation($"Using audio implementation: {_audioImplementation.GetType().Name}");

        // Perform conversion using the loaded implementation
        var convertedAudioCacheData = await _audioImplementation.ConvertAudioAsync(audioData, fileName, config, logger);

        var processingDuration = DateTime.UtcNow - conversionStart;

        logger.LogInformationWithCorrelation(
            $"Successfully converted audio file: {fileName} - " +
            $"Original: {audioData.Length} bytes, " +
            $"Duration: {processingDuration.TotalMilliseconds}ms");

        // Return the complete file cache data object directly
        return convertedAudioCacheData;
    }

    }
