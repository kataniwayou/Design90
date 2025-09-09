using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugin.Shared.Interfaces;
using Shared.Correlation;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Processor.PluginLoader.Services;

/// <summary>
/// Plugin manager for loading and managing plugin assemblies with two-level caching:
/// Level 1: PluginManager instances are cached by AssemblyBasePath (handled by PluginManagerFactory)
/// Level 2: Plugin instances are cached within PluginManager based on IsStateless flag
/// Provides isolated assembly loading contexts with dependency injection support.
/// </summary>
public class PluginManager : IDisposable
{
    private readonly string _baseAssemblyPath;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PluginManager> _logger;

    /// <summary>
    /// Level 2 Cache: Plugin instances cached by AssemblyName:Version:TypeName
    /// Only used when IsStateless = false
    /// </summary>
    private readonly ConcurrentDictionary<string, IPlugin> _pluginCache = new();

    public PluginManager(string baseAssemblyPath, IServiceProvider serviceProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAssemblyPath);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _baseAssemblyPath = baseAssemblyPath;
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<PluginManager>>();

        _logger.LogDebugWithCorrelation("🏗️ Created PluginManager for assembly path: {AssemblyBasePath}", baseAssemblyPath);
    }

    /// <summary>
    /// Loads a versioned plugin assembly with enhanced context and dependency resolution
    /// </summary>
    /// <param name="assemblyName">Name of the assembly to load</param>
    /// <param name="version">Version of the assembly</param>
    /// <returns>Loaded assembly</returns>
    public Assembly LoadPluginAssembly(string assemblyName, Version version)
    {
        // Create version-specific assembly path
        string versionedPath = Path.Combine(_baseAssemblyPath, $"v{version}", $"{assemblyName}.dll");

        if (!File.Exists(versionedPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {versionedPath}");
        }

        // Create shared context for this plugin version (isolation with shared infrastructure)
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        var contextLogger = loggerFactory.CreateLogger<SharedAssemblyLoadContext>();
        var context = new SharedAssemblyLoadContext(versionedPath, contextLogger);

        try
        {
            var assembly = context.LoadFromAssemblyPath(versionedPath);
            _logger.LogInformationWithCorrelation("✅ Loaded plugin assembly {AssemblyName} v{Version} with shared context isolation", assemblyName, version);
            return assembly;
        }
        catch (Exception ex)
        {
            context.Unload(); // Clean up on failure
            _logger.LogErrorWithCorrelation(ex, "❌ Failed to load plugin assembly: {AssemblyName} v{Version}", assemblyName, version);
            throw;
        }
    }

    /// <summary>
    /// Gets a plugin instance with conditional caching based on IsStateless flag.
    /// Level 2 caching: Plugin instances cached by AssemblyName:Version:TypeName when IsStateless = false
    /// </summary>
    /// <param name="assemblyName">Assembly name containing the plugin</param>
    /// <param name="version">Version of the assembly</param>
    /// <param name="typeName">Full type name of the plugin class</param>
    /// <param name="isStateless">If true, always create fresh instance; if false, cache and reuse instances</param>
    /// <returns>Plugin instance (fresh or cached based on isStateless parameter)</returns>
    public IPlugin GetPluginInstance(string assemblyName, Version version, string typeName, bool isStateless)
    {
        var cacheKey = GetPluginCacheKey(assemblyName, version, typeName);

        if (isStateless)
        {
            // Stateless mode: Remove from cache if exists and create fresh instance
            if (_pluginCache.TryRemove(cacheKey, out var existingPlugin))
            {
                _logger.LogDebugWithCorrelation("🗑️ Removed stateless plugin from cache: {CacheKey}", cacheKey);
                (existingPlugin as IDisposable)?.Dispose();
            }

            var freshInstance = CreateFreshPluginInstance(assemblyName, version, typeName);
            _logger.LogDebugWithCorrelation("🆕 Created fresh stateless plugin instance: {CacheKey}", cacheKey);
            return freshInstance;
        }
        else
        {
            // Stateful mode: Get from cache or create new and cache
            return _pluginCache.GetOrAdd(cacheKey, _ =>
            {
                var newInstance = CreateFreshPluginInstance(assemblyName, version, typeName);
                _logger.LogDebugWithCorrelation("💾 Cached new stateful plugin instance: {CacheKey}", cacheKey);
                return newInstance;
            });
        }
    }

    /// <summary>
    /// Generates cache key for plugin instances
    /// </summary>
    private string GetPluginCacheKey(string assemblyName, Version version, string typeName)
        => $"{assemblyName}:{version}:{typeName}";

    /// <summary>
    /// Gets the current count of cached plugin instances.
    /// Useful for monitoring and diagnostics.
    /// </summary>
    public int CachedPluginCount => _pluginCache.Count;

    /// <summary>
    /// Gets all cached plugin cache keys.
    /// Useful for monitoring and diagnostics.
    /// </summary>
    public IEnumerable<string> CachedPluginKeys => _pluginCache.Keys.ToList();

    /// <summary>
    /// Creates a fresh plugin instance with dependency injection
    /// </summary>
    private IPlugin CreateFreshPluginInstance(string assemblyName, Version version, string typeName)
    {
        // Load the assembly (this will still use existing assembly cache for performance)
        var assembly = LoadPluginAssembly(assemblyName, version);

        // Get the type
        var type = assembly.GetType(typeName);
        if (type == null)
        {
            throw new TypeLoadException($"Plugin type {typeName} not found in {assemblyName} v{version}");
        }

        // Validate that the type implements IPlugin
        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Plugin type {typeName} does not implement IPlugin");
        }

        _logger.LogDebugWithCorrelation("🔍 Creating fresh plugin instance: {PluginType}", type.Name);

        // Create plugin composite key for DI
        var pluginCompositeKey = $"{version}_{assemblyName}";
        _logger.LogDebugWithCorrelation("🔑 Plugin composite key: {CompositeKey}", pluginCompositeKey);

        try
        {
            // Create a scope and register the plugin composite key
            using var scope = _serviceProvider.CreateScope();
            var scopedServices = scope.ServiceProvider;

            // Create the instance with dependency injection, providing the composite key as an additional parameter
            var instance = ActivatorUtilities.CreateInstance(scopedServices, type, pluginCompositeKey);
            if (instance == null)
            {
                throw new InvalidOperationException($"Failed to create instance of plugin {type.Name}. ActivatorUtilities.CreateInstance returned null.");
            }

            _logger.LogDebugWithCorrelation("✅ Successfully created fresh plugin instance: {PluginType} with ActivatorUtilities and IServiceProvider injection", type.Name);

            return (IPlugin)instance;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "❌ Failed to create fresh plugin instance: {PluginType}", type.Name);
            throw new InvalidOperationException($"Unable to create instance of plugin {type.Name}. Ensure the plugin implements IPlugin and has a constructor that can be resolved by dependency injection.", ex);
        }
    }

    /// <summary>
    /// Disposes all cached plugin instances and clears the cache
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebugWithCorrelation("🧹 Disposing PluginManager and clearing plugin cache for path: {AssemblyBasePath}", _baseAssemblyPath);

        // Dispose all cached plugin instances
        foreach (var plugin in _pluginCache.Values)
        {
            try
            {
                (plugin as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarningWithCorrelation(ex, "⚠️ Error disposing plugin instance: {PluginType}", plugin.GetType().Name);
            }
        }

        // Clear the cache
        _pluginCache.Clear();

        _logger.LogDebugWithCorrelation("✅ PluginManager disposed successfully for path: {AssemblyBasePath}", _baseAssemblyPath);
    }

}

/// <summary>
/// Custom AssemblyLoadContext that provides plugin isolation while sharing critical infrastructure assemblies
/// </summary>
public class SharedAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblyNames;
    private readonly ILogger<SharedAssemblyLoadContext> _logger;

    public SharedAssemblyLoadContext(string pluginPath, ILogger<SharedAssemblyLoadContext> logger)
        : base($"Plugin_{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _logger = logger;

        // Define which assemblies should be shared with host (same type identities)
        _sharedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // .NET Core assemblies
            "System.Runtime",
            "System.Collections",
            "System.Threading",
            "System.Threading.Tasks",
            "netstandard",

            // Microsoft Extensions (DI infrastructure)
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.Configuration.Abstractions",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Hosting.Abstractions",

            // MassTransit (messaging infrastructure)
            "MassTransit",
            "MassTransit.Abstractions",

            // Your shared assemblies
            "Shared.Correlation",
            "Shared.Models",
            "Processor.Base",
            "Shared.MassTransit",
            "Shared.Configuration"
        };
    }

}
