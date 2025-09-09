using Microsoft.Extensions.Logging;
using System.Text.Json;
using Shared.Correlation;

namespace Plugin.Shared.Utilities;

/// <summary>
/// Helper utility for working with FileCacheDataObject structures
/// Provides methods to extract file content and metadata from cache data objects
/// </summary>
public static class FileCacheDataHelper
{
    /// <summary>
    /// Extracts file content as byte array from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file information</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>File content as byte array, or null if extraction fails</returns>
    public static byte[]? ExtractFileContent(object fileCacheDataObject, ILogger? logger = null)
    {
        try
        {
            if (fileCacheDataObject == null)
            {
                logger?.LogWarningWithCorrelation("FileCacheDataObject is null");
                return null;
            }

            // Serialize to JSON and parse to access properties
            var json = JsonSerializer.Serialize(fileCacheDataObject);
            var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Try to get fileContent.binaryData
            if (root.TryGetProperty("fileContent", out var fileContentElement) &&
                fileContentElement.TryGetProperty("binaryData", out var binaryDataElement))
            {
                var base64Data = binaryDataElement.GetString();
                if (!string.IsNullOrEmpty(base64Data))
                {
                    var fileContent = Convert.FromBase64String(base64Data);
                    logger?.LogDebugWithCorrelation("Successfully extracted file content: {Size} bytes", fileContent.Length);
                    return fileContent;
                }
            }

            logger?.LogWarningWithCorrelation("Could not find fileContent.binaryData in FileCacheDataObject");
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogErrorWithCorrelation(ex, "Failed to extract file content from FileCacheDataObject");
            return null;
        }
    }

    /// <summary>
    /// Extracts file name from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file information</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>File name, or null if extraction fails</returns>
    public static string? ExtractFileName(object fileCacheDataObject, ILogger? logger = null)
    {
        try
        {
            if (fileCacheDataObject == null)
            {
                logger?.LogWarningWithCorrelation("FileCacheDataObject is null");
                return null;
            }

            var json = JsonSerializer.Serialize(fileCacheDataObject);
            var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Try to get fileMetadata.fileName
            if (root.TryGetProperty("fileMetadata", out var metadataElement) &&
                metadataElement.TryGetProperty("fileName", out var fileNameElement))
            {
                var fileName = fileNameElement.GetString();
                logger?.LogDebugWithCorrelation("Successfully extracted file name: {FileName}", fileName);
                return fileName;
            }

            logger?.LogWarningWithCorrelation("Could not find fileMetadata.fileName in FileCacheDataObject");
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogErrorWithCorrelation(ex, "Failed to extract file name from FileCacheDataObject");
            return null;
        }
    }

    /// <summary>
    /// Extracts file metadata from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file information</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>File metadata object, or null if extraction fails</returns>
    public static FileMetadata? ExtractFileMetadata(object fileCacheDataObject, ILogger? logger = null)
    {
        try
        {
            if (fileCacheDataObject == null)
            {
                logger?.LogWarningWithCorrelation("FileCacheDataObject is null");
                return null;
            }

            var json = JsonSerializer.Serialize(fileCacheDataObject);
            var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("fileMetadata", out var metadataElement))
            {
                var metadata = new FileMetadata
                {
                    FileName = metadataElement.TryGetProperty("fileName", out var fn) ? fn.GetString() : null,
                    FilePath = metadataElement.TryGetProperty("filePath", out var fp) ? fp.GetString() : null,
                    FileSize = metadataElement.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0,
                    FileExtension = metadataElement.TryGetProperty("fileExtension", out var fe) ? fe.GetString() : null,
                    DetectedMimeType = metadataElement.TryGetProperty("detectedMimeType", out var mt) ? mt.GetString() : null,
                    FileType = metadataElement.TryGetProperty("fileType", out var ft) ? ft.GetString() : null,
                    ContentHash = metadataElement.TryGetProperty("contentHash", out var ch) ? ch.GetString() : null
                };

                if (metadataElement.TryGetProperty("createdDate", out var cd) && cd.TryGetDateTime(out var createdDate))
                    metadata.CreatedDate = createdDate;

                if (metadataElement.TryGetProperty("modifiedDate", out var md) && md.TryGetDateTime(out var modifiedDate))
                    metadata.ModifiedDate = modifiedDate;

                logger?.LogDebugWithCorrelation("Successfully extracted file metadata for: {FileName}", metadata.FileName);
                return metadata;
            }

            logger?.LogWarningWithCorrelation("Could not find fileMetadata in FileCacheDataObject");
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogErrorWithCorrelation(ex, "Failed to extract file metadata from FileCacheDataObject");
            return null;
        }
    }

    /// <summary>
    /// Analyzes audio content from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing audio file</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>Audio file information, or invalid result if analysis fails</returns>
    public static AudioFileInfo AnalyzeAudioFromCache(object fileCacheDataObject, ILogger? logger = null)
    {
        var fileContent = ExtractFileContent(fileCacheDataObject, logger);
        var fileName = ExtractFileName(fileCacheDataObject, logger);

        if (fileContent == null || string.IsNullOrEmpty(fileName))
        {
            return new AudioFileInfo 
            { 
                IsValid = false, 
                ErrorMessage = "Could not extract file content or name from FileCacheDataObject" 
            };
        }

        return AudioFileAnalyzer.AnalyzeAudioFile(fileContent, fileName, logger);
    }

    /// <summary>
    /// Applies XOR operation to file content from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file</param>
    /// <param name="key">XOR key (single byte)</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array, or null if operation fails</returns>
    public static byte[]? XorFileContentFromCache(object fileCacheDataObject, byte key, ILogger? logger = null)
    {
        var fileContent = ExtractFileContent(fileCacheDataObject, logger);
        if (fileContent == null)
        {
            logger?.LogWarningWithCorrelation("Could not extract file content for XOR operation");
            return null;
        }

        return XorUtility.XorWithByte(fileContent, key, logger);
    }

    /// <summary>
    /// Applies XOR operation to file content from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file</param>
    /// <param name="key">XOR key (byte array)</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array, or null if operation fails</returns>
    public static byte[]? XorFileContentFromCache(object fileCacheDataObject, byte[] key, ILogger? logger = null)
    {
        var fileContent = ExtractFileContent(fileCacheDataObject, logger);
        if (fileContent == null)
        {
            logger?.LogWarningWithCorrelation("Could not extract file content for XOR operation");
            return null;
        }

        return XorUtility.XorWithByteArray(fileContent, key, logger);
    }

    /// <summary>
    /// Applies XOR operation to file content from FileCacheDataObject
    /// </summary>
    /// <param name="fileCacheDataObject">The cache data object containing file</param>
    /// <param name="key">XOR key (string)</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array, or null if operation fails</returns>
    public static byte[]? XorFileContentFromCache(object fileCacheDataObject, string key, ILogger? logger = null)
    {
        var fileContent = ExtractFileContent(fileCacheDataObject, logger);
        if (fileContent == null)
        {
            logger?.LogWarningWithCorrelation("Could not extract file content for XOR operation");
            return null;
        }

        return XorUtility.XorWithString(fileContent, key, logger);
    }
}

/// <summary>
/// File metadata extracted from FileCacheDataObject
/// </summary>
public class FileMetadata
{
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public long FileSize { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? FileExtension { get; set; }
    public string? DetectedMimeType { get; set; }
    public string? FileType { get; set; }
    public string? ContentHash { get; set; }
}
