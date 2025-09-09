namespace Plugin.PreFileReader.Interfaces;

/// <summary>
/// Metrics service interface for PreFileReader plugin operations.
/// Provides OpenTelemetry-compatible metrics for monitoring plugin performance and health.
/// Aligned with ProcessorFlowMetricsService and ProcessorHealthMetricsService architecture.
/// </summary>
public interface IPreFileReaderPluginMetricsService : IDisposable
{
    // ========================================
    // PREFILEREADER-SPECIFIC METRICS
    // ========================================

    /// <summary>
    /// Records file discovery operation results
    /// </summary>
    void RecordFileDiscovery(long filesFound, string directoryPath, TimeSpan scanDuration,
        string correlationId, Guid orchestratedFlowEntityId, Guid stepId, Guid executionId);

    /// <summary>
    /// Records directory scanning operation
    /// </summary>
    void RecordDirectoryScan(bool success, string directoryPath, TimeSpan duration,
        string correlationId, Guid orchestratedFlowEntityId, Guid stepId, Guid executionId);



    // ========================================
    // EXCEPTION METRICS
    // ========================================

    /// <summary>
    /// Records plugin exception with severity level
    /// </summary>
    void RecordPluginException(string exceptionType, string severity,
        string correlationId, Guid orchestratedFlowEntityId, Guid stepId, Guid executionId);
}
