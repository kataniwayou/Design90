using Shared.Entities;
using Shared.Models;

namespace Manager.Orchestrator.Services;

/// <summary>
/// Interface for HTTP communication with other entity managers
/// </summary>
public interface IManagerHttpClient
{
    /// <summary>
    /// Retrieves the orchestrated flow entity by ID
    /// </summary>
    /// <param name="orchestratedFlowId">The orchestrated flow ID</param>
    /// <returns>The orchestrated flow entity</returns>
    Task<OrchestratedFlowEntity?> GetOrchestratedFlowAsync(Guid orchestratedFlowId);

    /// <summary>
    /// Retrieves step navigation data for the workflow
    /// </summary>
    /// <param name="workflowId">The workflow ID from the orchestrated flow</param>
    /// <returns>Tuple containing step navigation data dictionary and processor IDs list</returns>
    Task<(Dictionary<Guid, StepNavigationData> StepEntities, List<Guid> ProcessorIds)> GetStepManagerDataAsync(Guid workflowId);

    /// <summary>
    /// Retrieves assignment models grouped by step for the orchestrated flow
    /// </summary>
    /// <param name="orchestratedFlowId">The orchestrated flow ID</param>
    /// <returns>Dictionary with stepId as key and list of assignment models as value</returns>
    Task<Dictionary<Guid, List<AssignmentModel>>> GetAssignmentManagerDataAsync(Guid orchestratedFlowId);

    /// <summary>
    /// Retrieves schema definition by schema ID
    /// </summary>
    /// <param name="schemaId">The schema ID</param>
    /// <returns>Schema definition string</returns>
    Task<string> GetSchemaDefinitionAsync(Guid schemaId);
}
