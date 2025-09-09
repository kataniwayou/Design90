using Microsoft.Extensions.Logging;
using Shared.Correlation;

namespace Plugin.Shared.Utilities;

/// <summary>
/// Plugin utilities for file post-processing operations across all file processors
/// Provides standardized collision handling, logging, and file operations
/// </summary>
public static class FilePostProcessing
{
    /// <summary>
    /// Post-process a file based on configuration using standardized approach
    /// </summary>
    /// <typeparam name="TConfig">Configuration type that implements IFileProcessingConfiguration</typeparam>
    /// <param name="filePath">Path to the file to post-process</param>
    /// <param name="config">Configuration containing processing mode and settings</param>
    /// <param name="logger">Logger for correlation-aware logging</param>
    /// <returns>Task representing the operation</returns>
    public static Task PostProcessFileAsync<TConfig>(string filePath, TConfig config, ILogger logger)
        where TConfig : IFileProcessingConfiguration
    {
        try
        {
            switch (config.ProcessingMode)
            {
                case FileProcessingMode.LeaveUnchanged:
                    logger.LogDebugWithCorrelation("Left file unchanged: {FilePath}", filePath);
                    break;

                case FileProcessingMode.MarkAsProcessed:
                    MarkFileAsProcessed(filePath, config.ProcessedExtension, logger);
                    break;

                case FileProcessingMode.Delete:
                    File.Delete(filePath);
                    logger.LogInformationWithCorrelation("Deleted file: {FilePath}", filePath);
                    break;

                case FileProcessingMode.CopyToBackup:
                    CopyToBackupWithCollisionHandling(filePath, config, logger);
                    break;

                case FileProcessingMode.MoveToBackup:
                    MoveToBackupWithCollisionHandling(filePath, config, logger);
                    break;

                case FileProcessingMode.BackupAndMarkProcessed:
                    CopyToBackupWithCollisionHandling(filePath, config, logger);
                    MarkFileAsProcessed(filePath, config.ProcessedExtension, logger);
                    break;

                case FileProcessingMode.BackupAndDelete:
                    CopyToBackupWithCollisionHandling(filePath, config, logger);
                    File.Delete(filePath);
                    logger.LogInformationWithCorrelation("Deleted file after backup: {FilePath}", filePath);
                    break;

                default:
                    logger.LogWarningWithCorrelation("Unknown processing mode: {ProcessingMode}", config.ProcessingMode);
                    break;
            }

            logger.LogDebugWithCorrelation("Post-processing completed for: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Post-processing failed for: {FilePath}", filePath);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Generates a processed file path with collision handling using standardized naming pattern
    /// Uses pattern: {originalPath}{processedExtension} with counter for collisions
    /// </summary>
    /// <param name="filePath">Original file path</param>
    /// <param name="processedExtension">Extension to add (e.g., ".processed")</param>
    /// <returns>Processed file path with collision handling applied</returns>
    public static string GenerateProcessedPath(string filePath, string processedExtension)
    {
        var processedPath = filePath + processedExtension;

        // Handle collisions with counter
        var counter = 1;
        while (File.Exists(processedPath))
        {
            processedPath = $"{filePath}{processedExtension}_{counter}";
            counter++;
        }

        return processedPath;
    }

    /// <summary>
    /// Mark file as processed by adding extension using standardized approach with collision handling
    /// </summary>
    /// <param name="filePath">Path to the file to mark as processed</param>
    /// <param name="processedExtension">Extension to add (e.g., ".processed")</param>
    /// <param name="logger">Logger for correlation-aware logging</param>
    public static void MarkFileAsProcessed(string filePath, string processedExtension, ILogger logger)
    {
        var processedPath = GenerateProcessedPath(filePath, processedExtension);
        File.Move(filePath, processedPath);
        logger.LogInformationWithCorrelation("Marked file as processed: {ProcessedPath}", processedPath);
    }

    /// <summary>
    /// Generates a unique file path with collision handling using precise timestamp
    /// If file exists: Uses pattern {nameWithoutExt}_{timestamp_with_milliseconds}{extension}
    /// If file doesn't exist: Uses original filename without changes
    /// </summary>
    /// <param name="fileName">Original filename</param>
    /// <param name="targetDirectory">Target directory path</param>
    /// <returns>Unique file path with collision handling applied</returns>
    public static string GenerateUniqueFilePath(string fileName, string targetDirectory)
    {
        return GenerateOutputPath(fileName, targetDirectory);
    }

    /// <summary>
    /// Copy file to backup folder with collision handling using precise timestamp
    /// Uses pattern: {nameWithoutExt}_{timestamp_with_milliseconds}{extension}
    /// </summary>
    /// <param name="filePath">Path to the file to copy</param>
    /// <param name="config">Configuration containing backup folder</param>
    /// <param name="logger">Logger for correlation-aware logging</param>
    public static void CopyToBackupWithCollisionHandling(string filePath, IFileProcessingConfiguration config, ILogger logger)
    {
        if (!Directory.Exists(config.BackupFolder))
        {
            Directory.CreateDirectory(config.BackupFolder);
        }

        var fileName = Path.GetFileName(filePath);
        var backupPath = GenerateUniqueFilePath(fileName, config.BackupFolder);

        File.Copy(filePath, backupPath);
        logger.LogInformationWithCorrelation("Copied file to backup: {BackupPath}", backupPath);
    }

    /// <summary>
    /// Generates an output file path with collision handling using precise timestamp
    /// If file exists: Uses pattern {nameWithoutExt}_{timestamp_with_milliseconds}{extension}
    /// If file doesn't exist: Uses original filename without changes
    /// </summary>
    /// <param name="fileName">Original filename</param>
    /// <param name="outputDirectory">Output directory path</param>
    /// <returns>Output file path with collision handling applied</returns>
    public static string GenerateOutputPath(string fileName, string outputDirectory)
    {
        var baseFileName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // Start with original filename
        var outputFilePath = Path.Combine(outputDirectory, fileName);

        // If file doesn't exist, return original name without changes
        if (!File.Exists(outputFilePath))
        {
            return outputFilePath;
        }

        // File exists - use precise timestamp with milliseconds for uniqueness
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var timestampFileName = $"{baseFileName}_{timestamp}{extension}";
        outputFilePath = Path.Combine(outputDirectory, timestampFileName);

        return outputFilePath;
    }

    /// <summary>
    /// Move file to backup folder with collision handling using precise timestamp
    /// Uses pattern: {nameWithoutExt}_{timestamp_with_milliseconds}{extension}
    /// </summary>
    /// <param name="filePath">Path to the file to move</param>
    /// <param name="config">Configuration containing backup folder</param>
    /// <param name="logger">Logger for correlation-aware logging</param>
    public static void MoveToBackupWithCollisionHandling(string filePath, IFileProcessingConfiguration config, ILogger logger)
    {
        if (!Directory.Exists(config.BackupFolder))
        {
            Directory.CreateDirectory(config.BackupFolder);
        }

        var fileName = Path.GetFileName(filePath);
        var backupPath = GenerateUniqueFilePath(fileName, config.BackupFolder);

        File.Move(filePath, backupPath);
        logger.LogInformationWithCorrelation("Moved file to backup: {BackupPath}", backupPath);
    }
}
