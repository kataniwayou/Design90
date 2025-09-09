# Standardized HTTP Client Implementation

This document describes the standardized HTTP client implementation that provides consistent resilience patterns, logging, and timing across all manager services.

## Overview

The `BaseManagerHttpClient` class provides:
- **Standardized HTTP execution** with timing metrics
- **Resilience patterns** (retry + circuit breaker)
- **Consistent logging** with correlation IDs
- **Configurable policies** via appsettings.json
- **Type-safe response processing**

## Key Components

### 1. BaseManagerHttpClient
Abstract base class that all manager HTTP clients should inherit from.

### 2. IBaseManagerHttpClient
Interface defining standardized HTTP operations.

### 3. HttpClientConfiguration
Configuration class for resilience patterns and timeouts.

### 4. HttpClientServiceExtensions
Extension methods for dependency injection setup.

## Usage Example

### Step 1: Inherit from BaseManagerHttpClient

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Services;

namespace Manager.YourManager.Services;

public class ManagerHttpClient : BaseManagerHttpClient, IManagerHttpClient
{
    private readonly string _targetManagerBaseUrl;

    public ManagerHttpClient(HttpClient httpClient, IConfiguration configuration, ILogger<ManagerHttpClient> logger)
        : base(httpClient, configuration, logger)
    {
        _targetManagerBaseUrl = configuration["ManagerUrls:TargetManager"] ?? "http://localhost:5000";
    }

    public async Task<bool> CheckEntityExists(Guid entityId)
    {
        var url = $"{_targetManagerBaseUrl}/api/entity/{entityId}/exists";
        return await ExecuteEntityCheckAsync(url, "EntityExistenceCheck", entityId);
    }

    public async Task<EntityModel?> GetEntity(Guid entityId)
    {
        var url = $"{_targetManagerBaseUrl}/api/entity/{entityId}";
        return await ExecuteAndProcessResponseAsync<EntityModel>(url, "GetEntity", entityId);
    }
}
```

### Step 2: Configure in Program.cs

```csharp
// Add correlation ID support
builder.Services.AddCorrelationId();
builder.Services.AddHttpClientCorrelationSupport();

// Add standardized HTTP client
builder.Services.AddStandardizedHttpClient<IManagerHttpClient, ManagerHttpClient>(builder.Configuration);
```

### Step 3: Configure appsettings.json

```json
{
  "HttpClient": {
    "MaxRetries": 3,
    "RetryDelayMs": 1000,
    "CircuitBreakerThreshold": 3,
    "CircuitBreakerDurationSeconds": 30,
    "TimeoutSeconds": 30,
    "EnableDetailedMetrics": true
  },
  "ManagerUrls": {
    "Assignment": "http://localhost:5130",
    "Schema": "http://localhost:5160"
  }
}
```

## Benefits

### 1. Performance Monitoring
- Automatic timing for all HTTP requests
- Detailed metrics logging
- Operation-specific performance tracking

### 2. Consistent Error Handling
- Standardized retry logic
- Circuit breaker protection
- Fail-safe error responses

### 3. Observability
- Correlation ID propagation
- Structured logging
- Consistent log formats

### 4. Maintainability
- Single place to update HTTP logic
- Configurable resilience patterns
- Type-safe implementations

## Migration Guide

### Before (Original Implementation)
```csharp
private async Task<bool> ExecuteEntityCheck(string url, string operationName, Guid entityId)
{
    try
    {
        var response = await _resilientPolicy.ExecuteAsync(async () =>
        {
            var httpResponse = await _httpClient.GetAsync(url);
            return httpResponse;
        });

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            return bool.Parse(content);
        }
        // ... error handling
    }
    catch (Exception ex)
    {
        // ... error handling
    }
}
```

### After (Standardized Implementation)
```csharp
public async Task<bool> CheckEntityExists(Guid entityId)
{
    var url = $"{_baseUrl}/api/entity/{entityId}/exists";
    return await ExecuteEntityCheckAsync(url, "EntityExistenceCheck", entityId);
}
```

## Configuration Options

| Setting | Default | Description |
|---------|---------|-------------|
| MaxRetries | 3 | Maximum retry attempts |
| RetryDelayMs | 1000 | Base delay for exponential backoff |
| CircuitBreakerThreshold | 3 | Failures before circuit opens |
| CircuitBreakerDurationSeconds | 30 | Circuit open duration |
| TimeoutSeconds | 30 | HTTP client timeout |
| EnableDetailedMetrics | true | Include timing in logs |

## Advanced Usage

### Custom Resilience Policies
Override `CreateResilientPolicy()` in derived classes for custom requirements:

```csharp
protected override IAsyncPolicy<HttpResponseMessage> CreateResilientPolicy()
{
    // Custom policy implementation
    return base.CreateResilientPolicy();
}
```

### Custom Response Processing
Override `ProcessResponseAsync<T>()` for specialized response handling:

```csharp
protected override async Task<T?> ProcessResponseAsync<T>(HttpResponseMessage response, string url, string operationName, Guid? entityId = null)
{
    // Custom processing logic
    return await base.ProcessResponseAsync<T>(response, url, operationName, entityId);
}
```
