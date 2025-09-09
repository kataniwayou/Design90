using Microsoft.Extensions.Logging;
using Shared.Correlation;
using System.Reflection;

namespace Plugin.Standardizer.Services;

/// <summary>
/// Service for loading custom standardization implementations from current assembly
/// </summary>
public class ImplementationLoader : IImplementationLoader
{
    private readonly ILogger _logger;

    public ImplementationLoader(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Load implementation type from current assembly
    /// </summary>
    public T LoadImplementation<T>(string typeName) where T : class
    {
        try
        {
            _logger.LogDebugWithCorrelation($"Loading implementation: {typeName} from current assembly");

            // Get the type from current assembly
            var currentAssembly = Assembly.GetExecutingAssembly();
            var implementationType = currentAssembly.GetType(typeName);

            if (implementationType == null)
            {
                throw new InvalidOperationException($"Type '{typeName}' not found in current assembly '{currentAssembly.FullName}'");
            }

            // Verify it implements the required interface
            if (!typeof(T).IsAssignableFrom(implementationType))
            {
                throw new InvalidOperationException($"Type '{typeName}' does not implement '{typeof(T).Name}'");
            }

            // Create instance
            var instance = Activator.CreateInstance(implementationType) as T;
            if (instance == null)
            {
                throw new InvalidOperationException($"Failed to create instance of '{typeName}'");
            }

            _logger.LogInformationWithCorrelation($"Successfully loaded implementation: {typeName}");
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, $"Failed to load implementation: {typeName}");
            throw;
        }
    }
}
