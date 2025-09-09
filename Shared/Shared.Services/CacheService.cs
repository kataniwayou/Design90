using Hazelcast;
using Microsoft.Extensions.Logging;
using Shared.Correlation;
using Shared.Services.Interfaces;
using System.Diagnostics;
namespace Shared.Services;


/// <summary>
/// Hazelcast-specific cache service implementation
/// </summary>
public class CacheService : ICacheService
{
    private readonly Lazy<Task<IHazelcastClient>> _hazelcastClientFactory;
    private readonly ILogger<CacheService> _logger;
    private readonly ActivitySource _activitySource;

    public CacheService(
        Lazy<Task<IHazelcastClient>> hazelcastClientFactory,
        ILogger<CacheService> logger)
    {
        _hazelcastClientFactory = hazelcastClientFactory;
        _logger = logger;
        _activitySource = new System.Diagnostics.ActivitySource("BaseProcessorApplication.Cache");
    }

    private async Task<IHazelcastClient> GetClientAsync()
    {
        return await _hazelcastClientFactory.Value;
    }

    public async Task<string?> GetAsync(string mapName, string key)
    {
        using var activity = _activitySource.StartActivity("Cache.Get");
        activity?.SetTag("cache.operation", "get")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (processorId, orchestratedFlowId, correlationId, executionId, stepId, publishId) = ParseCacheKey(key);
            _logger.LogDebugWithCorrelation("Starting cache GET operation. MapName: {MapName}, Key: {Key}, ExecutionId: {ExecutionId}, StepId: {StepId}, OrchestratedFlowId: {OrchestratedFlowId}, ProcessorId: {ProcessorId}, PublishId: {PublishId}",
                mapName, key, executionId, stepId, orchestratedFlowId, processorId, publishId);

            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            _logger.LogDebugWithCorrelation("Hazelcast client obtained. MapName: {MapName}, Key: {Key}",
                mapName, key);

            var map = await client.GetMapAsync<string, string>(mapName);
            _logger.LogDebugWithCorrelation("Hazelcast map obtained. MapName: {MapName}, Key: {Key}",
                mapName, key);

            var result = await map.GetAsync(key);
            stopwatch.Stop();

            activity?.SetTag("cache.hit", result != null);
            activity?.SetTag("cache.operation_duration_ms", stopwatch.ElapsedMilliseconds);

            _logger.LogInformationWithCorrelation("Cache GET operation completed. MapName: {MapName}, Key: {Key}, ExecutionId: {ExecutionId}, StepId: {StepId}, ProcessorId: {ProcessorId}, Found: {Found}, Duration: {Duration}ms, ResultLength: {ResultLength}",
                mapName, key, executionId, stepId, processorId, result != null, stopwatch.ElapsedMilliseconds, result?.Length ?? 0);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("cache.operation_duration_ms", stopwatch.ElapsedMilliseconds);

            var (processorId, orchestratedFlowId, correlationId, executionId, stepId, publishId) = ParseCacheKey(key);
            _logger.LogErrorWithCorrelation(ex, "Failed to retrieve data from cache. MapName: {MapName}, Key: {Key}, ExecutionId: {ExecutionId}, StepId: {StepId}, ProcessorId: {ProcessorId}, PublishId: {PublishId}, Duration: {Duration}ms, ExceptionType: {ExceptionType}",
                mapName, key, executionId, stepId, processorId, publishId, stopwatch.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
    }

    public async Task SetAsync(string mapName, string key, string value)
    {
        using var activity = _activitySource.StartActivity("Cache.Set");
        activity?.SetTag("cache.operation", "set")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key);

        try
        {
            var (processorId, orchestratedFlowId, correlationId, executionId, stepId, publishId) = ParseCacheKey(key);

            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            await map.SetAsync(key, value);

            _logger.LogInformationWithCorrelation("Saved data to cache. MapName: {MapName}, Key: {Key}, ExecutionId: {ExecutionId}, StepId: {StepId}, OrchestratedFlowId: {OrchestratedFlowId}, ProcessorId: {ProcessorId}, PublishId: {PublishId}, DataLength: {DataLength}",
                mapName, key, executionId, stepId, orchestratedFlowId, processorId, publishId, value.Length);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to save data to cache. MapName: {MapName}, Key: {Key}",
                mapName, key);
            throw;
        }
    }

    public async Task SetAsync(string mapName, string key, string value, TimeSpan ttl)
    {
        using var activity = _activitySource.StartActivity("Cache.SetWithTtl");
        activity?.SetTag("cache.operation", "set_with_ttl")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key)
                ?.SetTag("cache.ttl_seconds", ttl.TotalSeconds);

        try
        {
            var (processorId, orchestratedFlowId, correlationId, executionId, stepId, publishId) = ParseCacheKey(key);

            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            await map.SetAsync(key, value, ttl);

            _logger.LogInformationWithCorrelation("Saved data to cache with TTL. MapName: {MapName}, Key: {Key}, ExecutionId: {ExecutionId}, StepId: {StepId}, ProcessorId: {ProcessorId}, PublishId: {PublishId}, TTL: {TTL}s, DataLength: {DataLength}",
                mapName, key, executionId, stepId, processorId, publishId, ttl.TotalSeconds, value.Length);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to save data to cache with TTL. MapName: {MapName}, Key: {Key}, TTL: {TTL}s",
                mapName, key, ttl.TotalSeconds);
            throw;
        }
    }



    public async Task<bool> ExistsAsync(string mapName, string key)
    {
        using var activity = _activitySource.StartActivity("Cache.Exists");
        activity?.SetTag("cache.operation", "exists")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key);

        try
        {
            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            var exists = await map.ContainsKeyAsync(key);

            activity?.SetTag("cache.hit", exists);

            _logger.LogDebugWithCorrelation("Checked cache key existence. MapName: {MapName}, Key: {Key}, Exists: {Exists}",
                mapName, key, exists);

            return exists;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to check cache key existence. MapName: {MapName}, Key: {Key}",
                mapName, key);
            throw;
        }
    }

    public async Task RemoveAsync(string mapName, string key)
    {
        using var activity = _activitySource.StartActivity("Cache.Remove");
        activity?.SetTag("cache.operation", "remove")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key);

        try
        {
            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            await map.RemoveAsync(key);

            _logger.LogDebugWithCorrelation("Removed data from cache. MapName: {MapName}, Key: {Key}",
                mapName, key);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to remove data from cache. MapName: {MapName}, Key: {Key}",
                mapName, key);
            throw;
        }
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // Simple health check by trying to get a map
            var client = await GetClientAsync();
            if (client == null)
            {
                return false;
            }

            var testMap = await client.GetMapAsync<string, string>("health-check");
            return testMap != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarningWithCorrelation(ex, "Hazelcast health check failed");
            return false;
        }
    }

    public async Task<(long entryCount, double averageAgeSeconds)> GetCacheStatisticsAsync(string mapName)
    {
        using var activity = _activitySource.StartActivity("Cache.GetStatistics");
        activity?.SetTag("cache.operation", "get_statistics")
                ?.SetTag("cache.map_name", mapName);

        try
        {
            var client = await GetClientAsync();
            if (client == null)
            {
                _logger.LogWarningWithCorrelation("Failed to obtain Hazelcast client for cache statistics");
                return (0, 0);
            }

            var map = await client.GetMapAsync<string, string>(mapName);

            // Get entry count
            var entryCount = await map.GetSizeAsync();

            // For average age, we would need to iterate through entries and parse their timestamps
            // This is a simplified implementation - in production you might want to store creation timestamps
            var averageAgeSeconds = 0.0; // Placeholder - would need more complex implementation

            _logger.LogDebugWithCorrelation("Retrieved cache statistics. MapName: {MapName}, EntryCount: {EntryCount}, AverageAge: {AverageAge}s",
                mapName, entryCount, averageAgeSeconds);

            return (entryCount, averageAgeSeconds);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to get cache statistics. MapName: {MapName}", mapName);
            return (0, 0);
        }
    }

    public string GetProcessorCacheKey(Guid processorId, Guid orchestratedFlowEntityId, Guid correlationId, Guid executionId, Guid stepId, Guid publishId)
    {
        return $"{processorId}:{orchestratedFlowEntityId}:{correlationId}:{executionId}:{stepId}:{publishId}";
    }

    /// <summary>
    /// Parses a cache key to extract its components for structured logging
    /// Supports both old format: {orchestratedFlowId}:{correlationId}:{executionId}:{stepId}:{publishId}
    /// and new format: {processorId}:{orchestratedFlowId}:{correlationId}:{executionId}:{stepId}:{publishId}
    /// </summary>
    /// <param name="cacheKey">The cache key to parse</param>
    /// <returns>Tuple containing processorId, orchestratedFlowId, correlationId, executionId, stepId, and publishId</returns>
    private (Guid processorId, Guid orchestratedFlowId, Guid correlationId, Guid executionId, Guid stepId, Guid publishId) ParseCacheKey(string cacheKey)
    {
        try
        {
            var parts = cacheKey.Split(':');

            // New format with processorId prefix: {processorId}:{orchestratedFlowId}:{correlationId}:{executionId}:{stepId}:{publishId}
            if (parts.Length >= 6)
            {
                var processorId = Guid.TryParse(parts[0], out var pId) ? pId : Guid.Empty;
                var orchestratedFlowId = Guid.TryParse(parts[1], out var flowId) ? flowId : Guid.Empty;
                var correlationId = Guid.TryParse(parts[2], out var cId) ? cId : Guid.Empty;
                var executionId = Guid.TryParse(parts[3], out var eId) ? eId : Guid.Empty;
                var stepId = Guid.TryParse(parts[4], out var sId) ? sId : Guid.Empty;
                var publishId = Guid.TryParse(parts[5], out var pubId) ? pubId : Guid.Empty;

                return (processorId, orchestratedFlowId, correlationId, executionId, stepId, publishId);
            }
            // Legacy format without processorId: {orchestratedFlowId}:{correlationId}:{executionId}:{stepId}:{publishId}
            else if (parts.Length >= 5)
            {
                var orchestratedFlowId = Guid.TryParse(parts[0], out var flowId) ? flowId : Guid.Empty;
                var correlationId = Guid.TryParse(parts[1], out var cId) ? cId : Guid.Empty;
                var executionId = Guid.TryParse(parts[2], out var eId) ? eId : Guid.Empty;
                var stepId = Guid.TryParse(parts[3], out var sId) ? sId : Guid.Empty;
                var publishId = Guid.TryParse(parts[4], out var pubId) ? pubId : Guid.Empty;

                return (Guid.Empty, orchestratedFlowId, correlationId, executionId, stepId, publishId);
            }
        }
        catch
        {
            // If parsing fails, return empty GUIDs
        }

        return (Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
    }

    public async Task<string?> PutIfAbsentAsync(string mapName, string key, string value)
    {
        using var activity = _activitySource.StartActivity("Cache.PutIfAbsent");
        activity?.SetTag("cache.operation", "putIfAbsent")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key);

        try
        {
            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            var previousValue = await map.PutIfAbsentAsync(key, value);

            var wasAbsent = previousValue == null;
            activity?.SetTag("cache.was_absent", wasAbsent);

            _logger.LogDebugWithCorrelation(
                "PutIfAbsent operation completed (using map TTL). MapName: {MapName}, Key: {Key}, WasAbsent: {WasAbsent}",
                mapName, key, wasAbsent);

            return previousValue;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to execute PutIfAbsent. MapName: {MapName}, Key: {Key}",
                mapName, key);
            throw;
        }
    }

    public async Task<string?> PutIfAbsentAsync(string mapName, string key, string value, TimeSpan ttl)
    {
        using var activity = _activitySource.StartActivity("Cache.PutIfAbsent");
        activity?.SetTag("cache.operation", "putIfAbsent")
                ?.SetTag("cache.map_name", mapName)
                ?.SetTag("cache.key", key)
                ?.SetTag("cache.ttl_seconds", ttl.TotalSeconds);

        try
        {
            var client = await GetClientAsync();
            if (client == null)
            {
                throw new InvalidOperationException("Failed to obtain Hazelcast client");
            }

            var map = await client.GetMapAsync<string, string>(mapName);
            var previousValue = await map.PutIfAbsentAsync(key, value, ttl);

            var wasAbsent = previousValue == null;
            activity?.SetTag("cache.was_absent", wasAbsent);

            _logger.LogDebugWithCorrelation(
                "PutIfAbsent operation completed. MapName: {MapName}, Key: {Key}, WasAbsent: {WasAbsent}, TTL: {TTL}s",
                mapName, key, wasAbsent, ttl.TotalSeconds);

            return previousValue;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogErrorWithCorrelation(ex, "Failed to execute PutIfAbsent. MapName: {MapName}, Key: {Key}",
                mapName, key);
            throw;
        }
    }

    public void Dispose()
    {
        _activitySource?.Dispose();
    }
}
