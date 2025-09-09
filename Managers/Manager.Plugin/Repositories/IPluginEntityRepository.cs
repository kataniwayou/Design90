
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Entities;
using Shared.Repositories.Interfaces;

namespace Manager.Plugin.Repositories;

public interface IPluginEntityRepository : IBaseRepository<PluginEntity>
{
    Task<IEnumerable<PluginEntity>> GetByVersionAsync(string version);
    Task<IEnumerable<PluginEntity>> GetByNameAsync(string name);
    Task<IEnumerable<PluginEntity>> GetByPayloadAsync(string payload);
    Task<IEnumerable<PluginEntity>> GetBySchemaIdAsync(Guid schemaId);

    /// <summary>
    /// Check if any plugin entities reference the specified schema ID
    /// </summary>
    /// <param name="schemaId">The schema ID to check for references</param>
    /// <returns>True if any plugin entities reference the schema, false otherwise</returns>
    Task<bool> HasSchemaReferences(Guid schemaId);

    /// <summary>
    /// Check if any plugin entities reference the specified input schema ID
    /// </summary>
    /// <param name="inputSchemaId">The input schema ID to check for references</param>
    /// <returns>True if any plugin entities reference the input schema, false otherwise</returns>
    Task<bool> HasInputSchemaReferences(Guid inputSchemaId);

    /// <summary>
    /// Check if any plugin entities reference the specified output schema ID
    /// </summary>
    /// <param name="outputSchemaId">The output schema ID to check for references</param>
    /// <returns>True if any plugin entities reference the output schema, false otherwise</returns>
    Task<bool> HasOutputSchemaReferences(Guid outputSchemaId);
}
